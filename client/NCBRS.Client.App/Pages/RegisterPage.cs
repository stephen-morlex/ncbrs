using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Storage;
using NCBRS.Models;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Registering a birth in five steps (the design handoff's mockups 4 and 5):
/// the child, the mother, the father, the marriage and proof of address, and
/// a review. Saved as a draft after every step, so a family that has to leave,
/// or a battery that dies, costs nothing typed.
///
/// **Splitting the form changes no rule.** The rules are the core's
/// (<see cref="RegistrationRules"/>, held to the registry's by parity tests).
/// Each step shows the problems the rules find in its own fields before moving
/// on, and the review checks everything again before a number is used.
///
/// **The review is the read-back.** The birth is shown back in words, with a
/// way to each step to put it right, and registering happens from there: a
/// number handed out is printed on a slip and cannot be taken back.
/// </summary>
public sealed class RegisterPage : FlowPage
{
    private const int StepCount = 5;

    private readonly DeviceHost _host;
    private readonly SyncBirthRecord? _correcting;
    private readonly Guid _draftId;
    private int _step = 1;

    /// <summary>
    /// The statutory window this birth is judged by: the tablet's default, or,
    /// correcting a birth the registry refused on the window, the registry's
    /// own — its ruling on this birth outranks the tablet's assumption.
    /// </summary>
    private readonly int _windowDays;

    // --- step 1: the child ---------------------------------------------------------------------------

    private readonly Entry _childGiven = Field(Strings.Register_ChildGivenNames);
    private readonly Entry _childSurname = Field(Strings.Register_ChildSurname);
    private readonly ChoiceChips<int> _day = new([(0, Strings.Records_Today), (1, Strings.Records_Yesterday)]);
    private readonly DatePicker _born = new() { MaximumDate = DateTime.Today, Date = DateTime.Today, Format = "d MMM yyyy" };

    // Required, and never defaulted: "this facility" is the commonest answer
    // at a hospital and the wrong one at a village post, whose births are
    // mostly at home.
    private readonly ChoiceChips<PlaceOfBirthKind> _place = Choices.Chips(Enum.GetValues<PlaceOfBirthKind>());
    private readonly Entry _placeWhere = Field(Strings.Register_PlaceOfBirthWhere);

    // Asked, never assumed: the enum's first value is Male, and a form that
    // defaulted to it would register every untouched answer as a boy.
    private readonly Tabs<Sex> _sex = new([
        (Sex.Female, Language.Name(Sex.Female)),
        (Sex.Male, Language.Name(Sex.Male)),
        (Sex.Undetermined, Language.Name(Sex.Undetermined)),
    ]);
    private readonly ChoiceChips<BirthPlurality> _plurality = Choices.Chips(Enum.GetValues<BirthPlurality>());
    private readonly Entry _order = Field(Strings.Register_Order, keyboard: Keyboard.Numeric);
    private readonly Entry _weight = Field(Strings.Register_WhatWeight, keyboard: Keyboard.Numeric);
    private readonly Entry _gestation = Field(Strings.Register_GestationShort, keyboard: Keyboard.Numeric);

    private readonly VerticalStackLayout _late;
    private readonly Label _lateNote = Ui.Body("", Ui.Theme.PendingForeground);
    private readonly Picker _evidence = Choices.Choice(Strings.Register_Evidence, Enum.GetValues<LateRegistrationEvidenceType>());
    private readonly Entry _evidenceReference = Field(Strings.Register_EvidenceReference);
    private readonly Entry _declarant = Field(Strings.Register_Declarant);
    private readonly Entry _relationship = Field(Strings.Register_Relationship);

    // --- step 2: the mother, and the optional statistics ---------------------------------------------

    private readonly ParentFields _mother = new(isMother: true);
    private readonly VerticalStackLayout _statistics;
    private readonly View _statisticsChevron;
    private bool _withStatistics;
    private readonly Picker _education = Choices.Choice(Strings.Register_Education, Enum.GetValues<EducationLevel>());
    private readonly Entry _priorLive = Field(Strings.Register_PriorLive, keyboard: Keyboard.Numeric);
    private readonly Entry _prenatal = Field(Strings.Register_Prenatal, keyboard: Keyboard.Numeric);

    // --- step 3 and 4: the father; the marriage and proof of address --------------------------------

    private readonly ParentFields _father = new(isMother: false);
    private readonly OptionalDate _married = new(Strings.Register_MarriageDateKnown, DateTime.Today.AddYears(-2));
    private readonly Entry _marriageCertificate = Field(Strings.Register_MarriageCertificate);
    private readonly Entry _proofKind = Field(Strings.Register_ProofKind);
    private readonly Entry _proofReference = Field(Strings.Register_ProofReference);

    // --- the frame -----------------------------------------------------------------------------------

    private readonly View[] _steps;
    private readonly ContentView _stepHost = new();
    private readonly Label _stepLabel = Ui.Caption("");
    private readonly Grid _progress = new() { ColumnSpacing = Space.Xs, HeightRequest = 6 };
    private readonly HorizontalStackLayout _saved;
    private readonly Button _back = Ui.OutlineButton(Strings.Register_Back);
    private readonly Button _next = Ui.PrimaryButton(Strings.Register_Next);
    private readonly Grid _buttons = new() { ColumnSpacing = Space.Md };

    /// <param name="correcting">A refused birth being corrected, or null for a new registration.</param>
    /// <param name="draft">A registration started earlier, to carry on with.</param>
    public RegisterPage(DeviceHost host, SyncBirthRecord? correcting = null, IReadOnlyList<ApiError>? reasons = null, FormDraft? draft = null)
        : base(correcting is null ? Strings.Register_Title : Strings.Register_CorrectTitle)
    {
        _host = host;
        _correcting = correcting;
        _draftId = draft?.Id ?? Guid.NewGuid();
        _windowDays = RegistrationRules.WindowStatedIn(reasons ?? []) ?? RegistrationRules.DefaultStatutoryWindowDays;

        _plurality.Selected = BirthPlurality.Singleton;
        _order.IsVisible = false;
        _plurality.SelectionChanged += (_, _) => _order.IsVisible = _plurality.Selected is not BirthPlurality.Singleton;

        // "Where" belongs to a birth away from this facility, and only then.
        _placeWhere.IsVisible = false;
        _place.SelectionChanged += (_, _) => _placeWhere.IsVisible = _place.HasSelection && _place.Selected != PlaceOfBirthKind.ThisFacility;

        _day.Selected = 0;
        _day.SelectionChanged += (_, _) =>
        {
            if (_day.HasSelection)
            {
                _born.Date = DateTime.Today.AddDays(-_day.Selected);
            }
        };
        _born.DateSelected += (_, _) =>
        {
            var date = (_born.Date ?? DateTime.Today).Date;
            if (date == DateTime.Today && !(_day.HasSelection && _day.Selected == 0))
            {
                _day.Selected = 0;
            }
            else if (date == DateTime.Today.AddDays(-1) && !(_day.HasSelection && _day.Selected == 1))
            {
                _day.Selected = 1;
            }
            else if (date < DateTime.Today.AddDays(-1) && _day.HasSelection)
            {
                _day.Clear();
            }

            ShowLateSection();
        };

        _late = new VerticalStackLayout
        {
            Spacing = Space.Lg,
            Children =
            {
                Ui.Heading(Strings.Register_Late), _lateNote,
                Ui.Labeled(_evidence), Ui.Labeled(_evidenceReference), Ui.Labeled(_declarant), Ui.Labeled(_relationship),
            },
        };

        _statisticsChevron = Ui.Icon(Icons.ChevronDown, Ui.TextMuted, 20);
        _statistics = new VerticalStackLayout
        {
            Spacing = Space.Xl,
            IsVisible = false,
            Children = { Ui.Labeled(_education), Ui.Labeled(_priorLive), Ui.Labeled(_prenatal) },
        };

        _saved = new HorizontalStackLayout
        {
            Spacing = Space.Xs,
            IsVisible = false,
            VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Icon(Icons.Tick, Ui.Primary, 16), Ui.Caption(Strings.Register_DraftSaved, Ui.Primary) },
        };

        _steps = [ChildStep(reasons), MotherStep(), FatherStep(), FamilyStep()];

        _back.Clicked += async (_, _) => await BackAsync();
        _next.Clicked += async (_, _) => await RunAsync(NextAsync);
        _buttons.ColumnDefinitions = [new ColumnDefinition(new GridLength(1, GridUnitType.Star)), new ColumnDefinition(new GridLength(2, GridUnitType.Star))];
        _buttons.Add(_back, 0);
        _buttons.Add(_next, 1);

        if (correcting is not null)
        {
            Fill(correcting.Birth, sexChosen: true, withStatistics: correcting.Birth.MaternalStatistics is not null);
        }
        else if (draft is not null)
        {
            Fill(draft.Birth, draft.SexChosen, draft.WithStatistics);
            _saved.IsVisible = true;
        }

        ShowLateSection();
        BuildFocused(Header(), new VerticalStackLayout { Spacing = Space.Md, Children = { Status, Busy, _buttons } }, _stepHost);
        Step(draft?.Step is { } reached ? Math.Clamp(reached, 1, StepCount) : 1);
    }

    /// <summary>When the birth was captured: now for a new one; for a correction, the original moment, which the window is measured to.</summary>
    private DateTime CapturedAt => _correcting?.Birth.RegisteredAtUtc ?? DateTime.UtcNow;

    // --- the steps -----------------------------------------------------------------------------------

    private View ChildStep(IReadOnlyList<ApiError>? reasons)
    {
        var step = new VerticalStackLayout { Spacing = Space.Xl };

        if (_correcting is not null)
        {
            // The registry's reasons first, in its own words, beside the field in the registrar's language.
            step.Add(Ui.Notice(Icons.Error,
                Strings.Register_RefusedBecause + "\n"
                + string.Join("\n", (reasons ?? []).Select(reason => $"• {Language.Field(reason.Field)}: {reason.Message}")),
                Tone.Danger));
            step.Add(Ui.Body(Language.Format(Strings.Register_CorrectNote, _correcting.Birth.Brn), Ui.TextMuted));
        }

        step.Add(Ui.SectionTitle(Strings.Register_AboutChild));
        step.Add(Ui.Labeled(_childGiven));
        step.Add(Ui.Labeled(_childSurname));

        var when = new Grid { ColumnSpacing = Space.Sm, ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)] };
        when.Add(_day, 0);
        when.Add(Ui.Input(_born, trailingIcon: Icons.Calendar), 1);
        step.Add(Ui.Labeled(Strings.Register_DateOfBirth, when));
        step.Add(Ui.ShownWith(Ui.Card(_late), _late));

        step.Add(Ui.Labeled(Strings.Register_PlaceOfBirth, _place));
        step.Add(Ui.Labeled(_placeWhere));
        step.Add(Ui.Labeled(Strings.Register_Sex, _sex));
        step.Add(Ui.Labeled(Strings.Register_HowMany, _plurality));
        step.Add(Ui.Labeled(_order));
        step.Add(Ui.Group(Strings.Register_AtBirth, Ui.Columns(2,
            Ui.Labeled(Strings.Register_WhatWeight, _weight, suffix: Strings.Unit_Grams),
            Ui.Labeled(Strings.Register_GestationShort, _gestation, suffix: Strings.Unit_Weeks))));
        return step;
    }

    private View MotherStep()
    {
        var header = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Md,
        };
        header.Add(Ui.IconBox(Icons.Add), 0);
        header.Add(new VerticalStackLayout
        {
            Spacing = Space.Xs,
            VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Heading(Strings.Register_StatisticsTitle), Ui.Caption(Strings.Register_Optional) },
        }, 1);
        header.Add(_statisticsChevron, 2);
        Ui.Tappable(header, () => OpenStatistics(!_withStatistics));
        SemanticProperties.SetDescription(header, Strings.Register_StatisticsTitle);

        return new VerticalStackLayout
        {
            Spacing = Space.Xl,
            Children =
            {
                Ui.SectionTitle(Strings.Register_AboutMother),
                _mother.View,
                // Never in the way: a birth whose questionnaire is blank is a
                // fully registered birth.
                Ui.Card(header, _statistics),
            },
        };
    }

    private View FatherStep() => new VerticalStackLayout
    {
        Spacing = Space.Xl,
        Children = { Ui.SectionTitle(Strings.Register_AboutFather), _father.View },
    };

    private View FamilyStep() => new VerticalStackLayout
    {
        Spacing = Space.Xl,
        Children =
        {
            Ui.SectionTitle(Strings.Register_AboutFamily),
            Ui.Group(Strings.Register_MarriageSection, Ui.Caption(Strings.Register_MarriageNote), _married.View),
            Ui.Labeled(_marriageCertificate),
            Ui.Group(Strings.Register_ProofSection, Ui.Caption(Strings.Register_SectionOptional)),
            Ui.Labeled(_proofKind),
            Ui.Labeled(_proofReference),
        },
    };

    /// <summary>
    /// The birth read back, a card per step with the way back to it. What was
    /// not given says so, rather than leaving a gap that reads as forgotten.
    /// </summary>
    private View ReviewStep()
    {
        var (birth, problems) = Check(CapturedAt);
        var review = new VerticalStackLayout { Spacing = Space.Xl };
        review.Add(Ui.PageHeader(Strings.Register_ReviewTitle, Strings.Register_ReviewNote));

        if (problems.Count > 0)
        {
            review.Add(Ui.Notice(Icons.Error,
                Strings.Register_ReviewProblems + "\n" + string.Join("\n", problems.Select(problem => "• " + problem.Message)),
                Tone.Danger));
        }

        var place = birth.PlaceOfBirthKind is { } kind
            ? Language.Name(kind) + (string.IsNullOrWhiteSpace(birth.PlaceOfBirth) ? "" : " · " + birth.PlaceOfBirth)
            : null;
        var child = new List<(string, string?)>
        {
            (Strings.Review_Name, BirthNames.Child(birth)),
            (Strings.Register_Sex, _sex.HasSelection ? Language.Name(birth.Sex) : null),
            (Strings.Register_DateOfBirth, birth.DateOfBirth.ToString("d MMM yyyy", Language.Current)),
            (Strings.Register_PlaceOfBirth, place),
            (Strings.Register_HowMany, Language.Name(birth.Plurality) + (birth.BirthOrder is { } order ? $" · {order}" : "")),
            (Strings.Register_WhatWeight, birth.BirthWeightGrams is { } grams ? $"{grams} {Strings.Unit_Grams}" : null),
            (Strings.Register_GestationShort, birth.GestationalAgeWeeks is { } weeks ? $"{weeks} {Strings.Unit_Weeks}" : null),
        };
        if (birth.LateRegistration is { } late)
        {
            child.Add((Strings.Register_Late, Language.Name(late.EvidenceType) + " · " + late.DeclarantName));
        }

        review.Add(Summary(Strings.Register_Child, 1, child));
        review.Add(Summary(Strings.Register_MotherSection, 2, ParentRows(birth.Mother, isMother: true)));
        if (birth.MaternalStatistics is { } statistics)
        {
            review.Add(Summary(Strings.Register_StatisticsTitle, 2, [
                (Strings.Register_Education, statistics.MotherEducationLevel is { } level ? Language.Name(level) : null),
                (Strings.Register_WhatPriorLive, statistics.PriorLiveBirths.ToString(CultureInfo.InvariantCulture)),
                (Strings.Register_WhatPrenatal, statistics.PrenatalVisitCount?.ToString(CultureInfo.InvariantCulture)),
            ]));
        }

        review.Add(Summary(Strings.Register_FatherSection, 3, ParentRows(birth.Father, isMother: false)));
        review.Add(Summary(Strings.Register_StepFamily, 4, [
            (Strings.Register_MarriageSection, birth.Marriage?.Date?.ToString("d MMM yyyy", Language.Current)),
            (Strings.Register_MarriageCertificate, birth.Marriage?.CertificateNumber),
            (Strings.Register_ProofKind, birth.ProofOfAddress?.Kind),
            (Strings.Register_ProofReference, birth.ProofOfAddress?.Reference),
        ]));

        if (_correcting is null && (HasInput() || _saved.IsVisible))
        {
            review.Add(Ui.Link(Strings.Register_Discard, () => _ = DiscardAsync()));
        }

        return review;
    }

    private static List<(string, string?)> ParentRows(ParentDetails? parent, bool isMother)
    {
        var name = parent is null ? null : PersonNamesOf(parent);
        var rows = new List<(string, string?)> { (Strings.Review_Name, name) };
        if (parent is null)
        {
            return rows;
        }

        if (isMother)
        {
            rows.Add((Strings.Register_MaidenSurname, parent.MaidenSurname));
        }

        rows.Add((Strings.Register_DateOfBirth, parent.DateOfBirth?.ToString("d MMM yyyy", Language.Current)));
        rows.Add((Strings.Register_ParentPlaceOfBirth, parent.PlaceOfBirth));
        rows.Add((Strings.Register_Occupation, parent.Occupation));
        rows.Add((Strings.Register_Address, parent.Address));
        rows.Add((Strings.Register_Document, parent.DocumentType is { } type ? Language.Name(type) + " · " + parent.DocumentNumber : null));
        return rows;
    }

    private static string? PersonNamesOf(ParentDetails parent)
    {
        var name = string.Join(' ', new[] { parent.GivenNames, parent.Surname }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return name.Length == 0 ? null : name;
    }

    /// <summary>One step's answers as label and value, with the way back to that step.</summary>
    private View Summary(string title, int step, IEnumerable<(string Label, string? Value)> rows)
    {
        var header = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        var heading = Ui.Heading(title);
        heading.VerticalOptions = LayoutOptions.Center;
        header.Add(heading, 0);
        header.Add(Ui.Link(Strings.Register_Edit, () => Step(step)), 1);

        var card = new VerticalStackLayout { Spacing = Space.Md, Children = { header } };
        foreach (var (label, value) in rows)
        {
            var row = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition(new GridLength(3, GridUnitType.Star))],
                ColumnSpacing = Space.Md,
            };
            row.Add(Ui.Caption(label), 0);
            var shown = Ui.Body(string.IsNullOrWhiteSpace(value) ? Strings.Register_NotGiven : value,
                string.IsNullOrWhiteSpace(value) ? Ui.TextMuted : null);
            row.Add(shown, 1);
            card.Add(row);
        }

        return Ui.CardOf(card);
    }

    // --- moving between steps ------------------------------------------------------------------------

    /// <summary>Read each time, not kept: the names follow the language the form is in.</summary>
    private static string[] StepNames =>
        [Strings.Register_Child, Strings.Register_MotherSection, Strings.Register_FatherSection, Strings.Register_StepFamily, Strings.Register_StepReview];

    private void Step(int step)
    {
        _step = step;
        Status.Text = "";
        _stepHost.Content = step == StepCount ? ReviewStep() : _steps[step - 1];
        _stepLabel.Text = Language.Format(Strings.Register_StepOf, step, StepCount, StepNames[step - 1]);

        _progress.Clear();
        _progress.ColumnDefinitions.Clear();
        for (var i = 1; i <= StepCount; i++)
        {
            _progress.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _progress.Add(new Border
            {
                BackgroundColor = i <= step ? Ui.Primary : Ui.Muted,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 3 },
                HeightRequest = 6,
            }, i - 1);
        }

        SemanticProperties.SetDescription(_progress, _stepLabel.Text);

        // The first step has nothing behind it: its one button is the way on.
        _back.IsVisible = step > 1;
        Grid.SetColumn(_next, step > 1 ? 1 : 0);
        Grid.SetColumnSpan(_next, step > 1 ? 1 : 2);
        _next.Text = step < StepCount ? Strings.Register_Next
            : _correcting is null ? Strings.Register_Submit : Strings.Register_SaveCorrection;

        _ = Scroller?.ScrollToAsync(0, 0, animated: false);
    }

    private async Task NextAsync()
    {
        if (_step == StepCount)
        {
            await FinishAsync();
            return;
        }

        // This step's own problems now, while the family is still here; the
        // rest are the later steps' to show.
        var problems = Check(CapturedAt).Problems.Where(problem => problem.Step == _step).ToList();
        if (problems.Count > 0)
        {
            await ShowProblemAsync(string.Join("\n", problems.Select(problem => problem.Message)));
            return;
        }

        await SaveDraftAsync();
        Step(_step + 1);
    }

    private async Task BackAsync()
    {
        if (_step > 1)
        {
            await SaveDraftAsync();
            Step(_step - 1);
            return;
        }

        await Go(new HomePage(_host));
    }

    /// <summary>
    /// Leaving keeps what was typed, as a draft, rather than asking first. A
    /// correction is not a draft: it is already a birth on the tablet, so
    /// leaving one half-done still asks.
    /// </summary>
    public override async Task<bool> CanLeaveAsync()
    {
        if (_correcting is not null)
        {
            return !HasInput() || await DisplayAlertAsync(Strings.Register_CorrectTitle, Strings.Register_LeaveClears,
                Strings.Common_Leave, Strings.Common_Cancel);
        }

        await SaveDraftAsync();
        return true;
    }

    private async Task SaveDraftAsync()
    {
        if (_correcting is not null || !HasInput())
        {
            return;
        }

        var birth = Read(CapturedAt, []);
        await _host.SaveDraftAsync(new FormDraft(_draftId, _step, DateTime.UtcNow, birth, _sex.HasSelection, _withStatistics));
        _saved.IsVisible = true;
    }

    private async Task DiscardAsync()
    {
        if (!await DisplayAlertAsync(Strings.Register_Discard, Strings.Register_DiscardConfirm, Strings.Register_Discard, Strings.Common_Cancel))
        {
            return;
        }

        await _host.DiscardDraftAsync(_draftId);
        Flow.Show(new HomePage(_host));
    }

    /// <summary>Everything checked again, then registered, or the registrar taken to the first step with a problem.</summary>
    private async Task FinishAsync()
    {
        var capturedAt = CapturedAt;
        var (birth, problems) = Check(capturedAt);
        if (problems.Count > 0)
        {
            Step(problems.Min(problem => problem.Step));
            await ShowProblemAsync(string.Join("\n", problems.Where(problem => problem.Step == _step).Select(problem => problem.Message)));
            return;
        }

        if (_correcting is not null)
        {
            if (!await DisplayAlertAsync(
                    Strings.Register_ConfirmCorrectionTitle,
                    Language.Format(Strings.Register_ConfirmCorrectionBody, BirthNames.Child(birth), birth.DateOfBirth, _correcting.Birth.Brn),
                    Strings.Common_Save, Strings.Common_GoBack))
            {
                return;
            }

            await _host.CorrectAsync(_correcting.Birth.Brn, birth);
            Flow.Show(new RefusedPage(_host));
            return;
        }

        var registered = await _host.RegisterAsync(birth);
        await _host.DiscardDraftAsync(_draftId);
        Flow.Show(new RegisteredPage(_host, registered, birth));
    }

    // --- reading the form ----------------------------------------------------------------------------

    /// <summary>
    /// Every problem with the birth as it stands, each with the step it
    /// belongs to: the form's own (a number that is not a number) and the
    /// registry's rules, in the registry's words beside the field in the
    /// registrar's language.
    /// </summary>
    private (RegisterBirthRequest Birth, List<(string Message, int Step)> Problems) Check(DateTime capturedAt)
    {
        var problems = new List<(string Message, int Step)>();
        var birth = Read(capturedAt, problems);
        foreach (var problem in RegistrationRules.Problems(birth, capturedAt, _windowDays))
        {
            // Said once, by the form, in the registrar's language.
            if ((problem.Field == "sex" && !_sex.HasSelection) || (problem.Field == "placeOfBirthKind" && !_place.HasSelection))
            {
                continue;
            }

            problems.Add(($"{Language.Field(problem.Field)}: {problem.Message}", StepOf(problem.Field)));
        }

        return (birth, problems);
    }

    private static int StepOf(string field)
        => field.StartsWith("mother", StringComparison.Ordinal) || field.StartsWith("maternalStatistics", StringComparison.Ordinal) ? 2
            : field.StartsWith("father", StringComparison.Ordinal) ? 3
            : field.StartsWith("marriage", StringComparison.Ordinal) || field.StartsWith("proofOfAddress", StringComparison.Ordinal) ? 4
            : 1;

    /// <summary>What the registrar entered, as the registry's request. Problems that are the form's own are added to <paramref name="problems"/>.</summary>
    private RegisterBirthRequest Read(DateTime capturedAt, List<(string Message, int Step)> problems)
    {
        int? Whole(Entry entry, string what, int step)
        {
            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                return null;
            }

            if (int.TryParse(Language.WesternDigits(entry.Text).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            problems.Add((Language.Format(Strings.Register_WholeNumber, what), step));
            return null;
        }

        decimal? weeks = null;
        if (!string.IsNullOrWhiteSpace(_gestation.Text))
        {
            if (decimal.TryParse(Language.WesternDigits(_gestation.Text).Trim().Replace(',', '.').Replace('٫', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                weeks = parsed;
            }
            else
            {
                problems.Add((Strings.Register_GestationNumber, 1));
            }
        }

        if (!_sex.HasSelection)
        {
            problems.Add((Strings.Register_ChooseSex, 1));
        }

        // Said by the form, in the registrar's language, whatever else is
        // missing: the registry asks for the place only once the child is
        // named in parts, so an empty form would otherwise not mention it.
        if (!_place.HasSelection)
        {
            problems.Add((Strings.Register_ChoosePlace, 1));
        }

        var plurality = _plurality.HasSelection ? _plurality.Selected : BirthPlurality.Singleton;

        LateRegistrationDetails? late = null;
        if (_late.IsVisible)
        {
            if (Choices.Picked<LateRegistrationEvidenceType>(_evidence) is not { } evidence)
            {
                problems.Add((Strings.Register_ChooseEvidence, 1));
            }
            else
            {
                late = new LateRegistrationDetails
                {
                    EvidenceType = evidence,
                    EvidenceReference = Text(_evidenceReference),
                    DeclarantName = Text(_declarant) ?? "",
                    DeclarantRelationship = Text(_relationship) ?? "",
                };
            }
        }

        MaternalStatisticsRequest? statistics = null;
        if (_withStatistics)
        {
            statistics = new MaternalStatisticsRequest
            {
                MotherEducationLevel = Choices.Picked<EducationLevel>(_education),
                PriorLiveBirths = Whole(_priorLive, Strings.Register_WhatPriorLive, 2) ?? 0,
                PrenatalVisitCount = Whole(_prenatal, Strings.Register_WhatPrenatal, 2),
            };
        }

        PlaceOfBirthKind? placeKind = _place.HasSelection ? _place.Selected : null;
        var marriage = _married.Value is null && Text(_marriageCertificate) is null
            ? null
            : new MarriageDetails { Date = _married.Value, CertificateNumber = Text(_marriageCertificate) };
        var proof = Text(_proofKind) is null && Text(_proofReference) is null
            ? null
            : new ProofOfAddressDetails { Kind = Text(_proofKind), Reference = Text(_proofReference) };

        return new RegisterBirthRequest
        {
            // Always the fuller form: the child in parts, which is what makes
            // the registry require the place of birth too. A blank name is
            // sent blank, for the rules to say so in the registry's words.
            ChildGivenNames = Text(_childGiven) ?? "",
            ChildSurname = Text(_childSurname) ?? "",
            DateOfBirth = BornOn,
            PlaceOfBirthKind = placeKind,
            // A "where" typed before switching back to this facility is not sent.
            PlaceOfBirth = placeKind is PlaceOfBirthKind.ThisFacility ? null : Text(_placeWhere),
            Sex = _sex.HasSelection ? _sex.Selected : default,
            Plurality = plurality,
            BirthOrder = plurality == BirthPlurality.Singleton ? null : Whole(_order, Strings.Register_WhatOrder, 1),
            BirthWeightGrams = Whole(_weight, Strings.Register_WhatWeight, 1),
            GestationalAgeWeeks = weeks,
            Mother = _mother.Read(),
            Father = _father.Read(),
            Marriage = marriage,
            ProofOfAddress = proof,
            RegisteredAtUtc = capturedAt,
            LateRegistration = late,
            MaternalStatistics = statistics,
        };
    }

    private DateTime BornOn => DateTime.SpecifyKind((_born.Date ?? DateTime.Today).Date, DateTimeKind.Utc);

    /// <summary>Shown, and required, exactly when the registry will treat the birth as late.</summary>
    private void ShowLateSection()
    {
        _late.IsVisible = RegistrationRules.IsLate(BornOn, CapturedAt, _windowDays);
        _lateNote.Text = Language.Format(Strings.Register_LateNote, _windowDays);
    }

    private void OpenStatistics(bool open)
    {
        _withStatistics = open;
        _statistics.IsVisible = open;
        _statisticsChevron.Rotation = open ? 180 : 0;
    }

    /// <summary>The form, filled from a birth as it was sent or as a draft left it.</summary>
    private void Fill(RegisterBirthRequest birth, bool sexChosen, bool withStatistics)
    {
        // A birth from before the fuller form is put back in parts, split at
        // its last word; the registrar sees the split and can change it.
        var (given, surname) = birth.UsesStructuredNames
            ? (birth.ChildGivenNames, birth.ChildSurname)
            : ParentFields.Split(birth.ChildFullName);
        _childGiven.Text = given;
        _childSurname.Text = surname;
        _born.Date = birth.DateOfBirth.Date;
        var day = (DateTime.Today - birth.DateOfBirth.Date).Days;
        if (day is 0 or 1)
        {
            _day.Selected = day;
        }
        else
        {
            _day.Clear();
        }

        if (birth.PlaceOfBirthKind is { } kind)
        {
            _place.Selected = kind;
        }

        _placeWhere.Text = birth.PlaceOfBirth;
        if (sexChosen)
        {
            _sex.Selected = birth.Sex;
        }

        _plurality.Selected = birth.Plurality;
        _order.Text = birth.BirthOrder?.ToString(CultureInfo.InvariantCulture);
        _weight.Text = birth.BirthWeightGrams?.ToString(CultureInfo.InvariantCulture);
        _gestation.Text = birth.GestationalAgeWeeks?.ToString(CultureInfo.InvariantCulture);
        _mother.Fill(birth.Mother, birth.MotherFullName);
        _father.Fill(birth.Father, birth.FatherFullName);
        _married.Set(birth.Marriage?.Date);
        _marriageCertificate.Text = birth.Marriage?.CertificateNumber;
        _proofKind.Text = birth.ProofOfAddress?.Kind;
        _proofReference.Text = birth.ProofOfAddress?.Reference;
        ShowLateSection();

        if (birth.LateRegistration is { } late)
        {
            Choices.Select<LateRegistrationEvidenceType>(_evidence, late.EvidenceType);
            _evidenceReference.Text = late.EvidenceReference;
            _declarant.Text = late.DeclarantName;
            _relationship.Text = late.DeclarantRelationship;
        }

        OpenStatistics(withStatistics || birth.MaternalStatistics is not null);
        if (birth.MaternalStatistics is { } statistics)
        {
            Choices.Select(_education, statistics.MotherEducationLevel);
            _priorLive.Text = statistics.PriorLiveBirths.ToString(CultureInfo.InvariantCulture);
            _prenatal.Text = statistics.PrenatalVisitCount?.ToString(CultureInfo.InvariantCulture);
        }
    }

    private Entry[] Entries =>
    [
        _childGiven, _childSurname, _placeWhere, _order, _weight, _gestation, _marriageCertificate, _proofKind, _proofReference,
        _evidenceReference, _declarant, _relationship, _priorLive, _prenatal,
    ];

    private bool HasInput()
        => Entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Text))
           || _mother.HasInput || _father.HasInput || _married.HasInput
           || _sex.HasSelection || _place.HasSelection || _evidence.SelectedIndex >= 0 || _withStatistics;

    // --- the header ----------------------------------------------------------------------------------

    /// <summary>The way back, the title and "Draft saved", then the step and a bar showing how far.</summary>
    private View Header()
    {
        var back = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            WidthRequest = Ui.TouchTarget,
            HeightRequest = Ui.TouchTarget,
            Content = Ui.Icon(Icons.ChevronBack, Ui.Text, 24, directional: true),
        };
        SemanticProperties.SetDescription(back, Strings.Register_Back);
        Ui.Tappable(back, () => _ = BackAsync());

        var top = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Sm,
        };
        top.Add(back, 0);
        var title = Ui.Heading(Title);
        title.FontSize = 18;
        title.VerticalOptions = LayoutOptions.Center;
        top.Add(title, 1);
        top.Add(_saved, 2);

        return new VerticalStackLayout
        {
            Padding = new Thickness(Space.Md, Space.Sm, Space.Xl, Space.Md),
            Spacing = Space.Sm,
            Children =
            {
                top,
                new VerticalStackLayout
                {
                    Padding = new Thickness(Space.Sm, 0, 0, 0),
                    Spacing = Space.Sm,
                    Children = { _stepLabel, _progress },
                },
            },
        };
    }

    private static string? Text(Entry entry) => string.IsNullOrWhiteSpace(entry.Text) ? null : entry.Text.Trim();
}
