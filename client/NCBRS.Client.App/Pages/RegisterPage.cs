using System.Globalization;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Network;
using NCBRS.Models;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Registering a birth (B4) — a FIRST CUT, over the registry's own request,
/// to be redesigned with midwives and community health workers. The layout,
/// wording and order will change; the rules will not, because they are the
/// core's (<see cref="RegistrationRules"/>), held to the registry's validator
/// and statutory-window decision by parity tests.
///
/// Every refusal the registry would give is caught here, while the family is
/// still present, and the registrar confirms before anything is saved: a
/// number handed out is printed on a slip and cannot be taken back.
/// </summary>
public sealed class RegisterPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly Label _result = Ui.HideWhenEmpty(Ui.Body(""));
    private readonly Button _printSlip = Ui.SecondaryButton(Strings.Register_PrintSlip);

    /// <summary>The last birth's slip, until the next one: a family may need it printed again before leaving.</summary>
    private NCBRS.Client.Printing.PrintedDocument? _lastSlip;

    private readonly Entry _childGiven = Field(Strings.Register_ChildGivenNames);
    private readonly Entry _childSurname = Field(Strings.Register_ChildSurname);
    private readonly DatePicker _born = new() { MaximumDate = DateTime.Today, Date = DateTime.Today, Format = "d MMM yyyy" };

    // Required, and never defaulted: "this facility" is the commonest answer
    // at a hospital and the wrong one at a village post, whose births are
    // mostly at home.
    private readonly Picker _placeKind = Choice<PlaceOfBirthKind>(Strings.Register_PlaceOfBirth, Enum.GetValues<PlaceOfBirthKind>());
    private readonly Entry _placeWhere = Field(Strings.Register_PlaceOfBirthWhere);
    private readonly Picker _sex = Choice<Sex>(Strings.Register_Sex, [Sex.Female, Sex.Male, Sex.Undetermined]);
    private readonly Picker _plurality = Choice<BirthPlurality>(Strings.Register_Plurality, Enum.GetValues<BirthPlurality>());
    private readonly Entry _order = Field(Strings.Register_Order, keyboard: Keyboard.Numeric);
    private readonly Entry _weight = Field(Strings.Register_Weight, keyboard: Keyboard.Numeric);
    private readonly Entry _gestation = Field(Strings.Register_Gestation, keyboard: Keyboard.Numeric);

    private readonly ParentFields _mother = new(Strings.Register_MotherSection, isMother: true);
    private readonly ParentFields _father = new(Strings.Register_FatherSection, isMother: false);

    private readonly OptionalDate _married = new(Strings.Register_MarriageDateKnown, DateTime.Today.AddYears(-2));
    private readonly Entry _marriageCertificate = Field(Strings.Register_MarriageCertificate);
    private readonly Collapsible _marriage;

    private readonly Entry _proofKind = Field(Strings.Register_ProofKind);
    private readonly Entry _proofReference = Field(Strings.Register_ProofReference);
    private readonly Collapsible _proof;

    private readonly VerticalStackLayout _late;
    private readonly Label _lateNote = Ui.Body("", Ui.Theme.PendingForeground);
    private readonly Picker _evidence = Choice<LateRegistrationEvidenceType>(Strings.Register_Evidence, Enum.GetValues<LateRegistrationEvidenceType>());
    private readonly Entry _evidenceReference = Field(Strings.Register_EvidenceReference);
    private readonly Entry _declarant = Field(Strings.Register_Declarant);
    private readonly Entry _relationship = Field(Strings.Register_Relationship);

    private readonly CheckBox _withStatistics = new();
    private readonly VerticalStackLayout _statistics;
    private readonly Picker _education = Choice<EducationLevel>(Strings.Register_Education, Enum.GetValues<EducationLevel>());
    private readonly Entry _priorLive = Field(Strings.Register_PriorLive, keyboard: Keyboard.Numeric);
    private readonly Entry _prenatal = Field(Strings.Register_Prenatal, keyboard: Keyboard.Numeric);

    /// <summary>A refused birth being corrected, or null for a new registration.</summary>
    private readonly SyncBirthRecord? _correcting;

    /// <summary>
    /// When the birth was captured. A new one is captured now; a correction
    /// keeps the original moment, because the statutory window is measured to
    /// it — correcting a typo must not make a birth late.
    /// </summary>
    private DateTime CapturedAt => _correcting?.Birth.RegisteredAtUtc ?? DateTime.UtcNow;

    /// <summary>
    /// The statutory window this birth is judged by: the tablet's default, or,
    /// correcting a birth the registry refused on the window, the registry's
    /// own — its ruling on this birth outranks the tablet's assumption.
    /// </summary>
    private readonly int _windowDays;

    public RegisterPage(DeviceHost host, SyncBirthRecord? correcting = null, IReadOnlyList<ApiError>? reasons = null)
        : base(correcting is null ? Strings.Register_Title : Strings.Register_CorrectTitle)
    {
        _host = host;
        _correcting = correcting;
        _windowDays = RegistrationRules.WindowStatedIn(reasons ?? []) ?? RegistrationRules.DefaultStatutoryWindowDays;
        _plurality.SelectedIndex = 0;
        _order.IsVisible = false;
        _plurality.SelectedIndexChanged += (_, _) => _order.IsVisible = Picked<BirthPlurality>(_plurality) is not BirthPlurality.Singleton;

        // "Where" belongs to a birth away from this facility, and only then.
        _placeWhere.IsVisible = false;
        _placeKind.SelectedIndexChanged += (_, _) =>
            _placeWhere.IsVisible = Picked<PlaceOfBirthKind>(_placeKind) is { } kind && kind != PlaceOfBirthKind.ThisFacility;

        _marriage = new Collapsible(Strings.Register_MarriageSection,
            new Label { Text = Strings.Register_MarriageNote, FontSize = 12 }, _married.View, _marriageCertificate);
        _proof = new Collapsible(Strings.Register_ProofSection, _proofKind, _proofReference);

        _late = Section(Strings.Register_Late, _lateNote, _evidence, _evidenceReference, _declarant, _relationship);
        _born.DateSelected += (_, _) => ShowLateSection();
        ShowLateSection();

        _statistics = Section(Strings.Register_Statistics, _education, _priorLive, _prenatal);
        _statistics.IsVisible = false;
        _withStatistics.CheckedChanged += (_, args) => _statistics.IsVisible = args.Value;

        var register = Ui.PrimaryButton(Strings.Register_Submit);
        register.Clicked += async (_, _) => await RunAsync(RegisterAsync);
        _printSlip.IsVisible = false;
        _printSlip.Clicked += async (_, _) => await RunAsync(PrintSlipAsync);

        // In cards, by what they describe, every field labelled above it: a
        // placeholder vanishes once something is typed, and three name fields
        // for a mother with no labels are three guesses.
        var form = new View[]
        {
            Ui.Card(
                Ui.Heading(Strings.Register_Child),
                Labeled(_childGiven), Labeled(_childSurname),
                Labeled(Strings.Register_DateOfBirth, _born),
                Labeled(_placeKind), Labeled(_placeWhere),
                Labeled(_sex), Labeled(_plurality), Labeled(_order),
                Labeled(_weight), Labeled(_gestation)),
            Ui.Card(
                Ui.Heading(Strings.Register_Parents),
                _mother.Section.Header, _mother.Section.Content,
                _father.Section.Header, _father.Section.Content,
                _marriage.Header, _marriage.Content,
                _proof.Header, _proof.Content),
            ShownWith(Ui.Card(_late), _late),
            Ui.Card(
                new HorizontalStackLayout { Spacing = Space.Sm, Children = { _withStatistics, new Label { Text = Strings.Register_AddStatistics, FontSize = 16, FontFamily = Ui.Regular, TextColor = Ui.Text, VerticalOptions = LayoutOptions.Center } } },
                _statistics),
            Status, Busy,
        };

        if (correcting is not null)
        {
            // The registry's reasons first, then the birth as it was sent, to put right.
            Fill(correcting.Birth);
            register.Text = Strings.Register_SaveCorrection;
            var back = Ui.GhostButton(Strings.Register_BackNoSave);
            back.Clicked += (_, _) => Flow.Show(new RefusedPage(host));
            BuildInside(host, Pages.Section.Register, [
                Heading(Language.Format(Strings.Register_CorrectHeading, correcting.Birth.Brn)),
                // The field in the registrar's language; the reason in the registry's words.
                Ui.Notice(Icons.Error,
                    Strings.Register_RefusedBecause + "\n"
                    + string.Join("\n", (reasons ?? []).Select(reason => $"• {Language.Field(reason.Field)}: {reason.Message}")),
                    Tone.Danger),
                Note(Language.Format(Strings.Register_CorrectNote, correcting.Birth.Brn)),
                .. form, register, back]);
            return;
        }

        BuildInside(host, Pages.Section.Register, [Ui.Title(Strings.Register_Title), .. form, register, _result, _printSlip]);
    }

    /// <summary>Leaving the form drops what is typed in it, so a half-typed birth asks first.</summary>
    public override async Task<bool> CanLeaveAsync()
        => !HasInput() || await DisplayAlertAsync(Strings.Register_Title, Strings.Register_LeaveClears,
            Strings.Common_Leave, Strings.Common_Cancel);

    private static View Labeled(View input) => Ui.Labeled(input);

    private static View Labeled(string label, View input) => Ui.Labeled(label, input);

    private static View ShownWith(View wrapper, View inner) => Ui.ShownWith(wrapper, inner);

    /// <summary>The form, filled from a birth as it was sent.</summary>
    private void Fill(RegisterBirthRequest birth)
    {
        // A birth from before the fuller form is put back in parts, split at
        // its last word; the registrar sees the split and can change it.
        var (given, surname) = birth.UsesStructuredNames
            ? (birth.ChildGivenNames, birth.ChildSurname)
            : ParentFields.Split(birth.ChildFullName);
        _childGiven.Text = given;
        _childSurname.Text = surname;
        _born.Date = birth.DateOfBirth.Date;
        Choices.Select(_placeKind, birth.PlaceOfBirthKind);
        _placeWhere.Text = birth.PlaceOfBirth;
        Select(_sex, birth.Sex);
        Select(_plurality, birth.Plurality);
        _order.Text = birth.BirthOrder?.ToString(CultureInfo.InvariantCulture);
        _weight.Text = birth.BirthWeightGrams?.ToString(CultureInfo.InvariantCulture);
        _gestation.Text = birth.GestationalAgeWeeks?.ToString(CultureInfo.InvariantCulture);
        _mother.Fill(birth.Mother, birth.MotherFullName);
        _father.Fill(birth.Father, birth.FatherFullName);
        _married.Set(birth.Marriage?.Date);
        _marriageCertificate.Text = birth.Marriage?.CertificateNumber;
        _marriage.Open(birth.Marriage is not null);
        _proofKind.Text = birth.ProofOfAddress?.Kind;
        _proofReference.Text = birth.ProofOfAddress?.Reference;
        _proof.Open(birth.ProofOfAddress is not null);
        ShowLateSection();

        if (birth.LateRegistration is { } late)
        {
            Select(_evidence, late.EvidenceType);
            _evidenceReference.Text = late.EvidenceReference;
            _declarant.Text = late.DeclarantName;
            _relationship.Text = late.DeclarantRelationship;
        }

        if (birth.MaternalStatistics is { } statistics)
        {
            _withStatistics.IsChecked = true;
            if (statistics.MotherEducationLevel is { } education)
            {
                Select(_education, education);
            }

            _priorLive.Text = statistics.PriorLiveBirths.ToString(CultureInfo.InvariantCulture);
            _prenatal.Text = statistics.PrenatalVisitCount?.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void Select<T>(Picker picker, T value) where T : struct, Enum => Choices.Select<T>(picker, value);

    /// <summary>Shown, and required, exactly when the registry will treat the birth as late.</summary>
    private void ShowLateSection()
    {
        var late = RegistrationRules.IsLate(BornOn, CapturedAt, _windowDays);
        _late.IsVisible = late;
        _lateNote.Text = Language.Format(Strings.Register_LateNote, _windowDays);
    }

    private DateTime BornOn => DateTime.SpecifyKind((_born.Date ?? DateTime.Today).Date, DateTimeKind.Utc);

    private async Task RegisterAsync()
    {
        var capturedAt = CapturedAt;
        // Every problem at once — the form's own and the registry's — so the
        // registrar fixes them in one pass, not one tap each.
        var problems = Read(capturedAt, out var birth)
            .Concat(RegistrationRules.Problems(birth, capturedAt, _windowDays)
                .Where(problem => !(problem.Field == "sex" && Picked<Sex>(_sex) is null))
                .Where(problem => !(problem.Field == "placeOfBirthKind" && Picked<PlaceOfBirthKind>(_placeKind) is null))
                // The field in the registrar's language; the rule in the registry's
                // words, which the tablet's copy is held to by parity tests.
                .Select(problem => $"{Language.Field(problem.Field)}: {problem.Message}"))
            .ToList();
        if (problems.Count > 0)
        {
            await ShowProblemAsync(string.Join("\n", problems));
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

        // Confirmed before a number is used: once on a slip it cannot be taken back.
        if (!await DisplayAlertAsync(
                Strings.Register_ConfirmTitle,
                Language.Format(Strings.Register_ConfirmBody, BirthNames.Child(birth), Language.Name(birth.Sex), birth.DateOfBirth)
                + (BirthNames.Mother(birth) is { } mother ? Language.Format(Strings.Register_ConfirmMother, mother) : "")
                + (birth.LateRegistration is not null ? Strings.Register_ConfirmLate : Strings.Register_ConfirmEnd),
                Strings.Register_ConfirmYes, Strings.Common_GoBack))
        {
            return;
        }

        var draft = await _host.RegisterAsync(birth);
        _result.Text = draft.IsProvisional
            ? Language.Format(Strings.Register_Provisional, draft.Brn)
            : Language.Format(Strings.Register_Registered, draft.Brn);
        if (draft.BlockLow)
        {
            _result.Text += "\n" + Strings.Register_BlockLow;
        }

        _lastSlip = _host.SlipFor(draft, birth);
        _printSlip.IsVisible = _host.Printer is not null;
        Clear();
    }

    private async Task PrintSlipAsync()
    {
        if (_lastSlip is null || _host.Printer is null)
        {
            return;
        }

        if (await _host.Printer.PrintAsync(_lastSlip) is { } problem)
        {
            await ShowProblemAsync(problem);
        }
    }

    /// <summary>What the registrar entered, as the registry's request. Problems that are the form's own — a number that is not a number — come back first.</summary>
    private List<string> Read(DateTime capturedAt, out RegisterBirthRequest birth)
    {
        var problems = new List<string>();
        int? Whole(Entry entry, string what)
        {
            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                return null;
            }

            if (int.TryParse(Language.WesternDigits(entry.Text).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            problems.Add(Language.Format(Strings.Register_WholeNumber, what));
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
                problems.Add(Strings.Register_GestationNumber);
            }
        }

        // Asked, never assumed: the enum's first value is Male, and a form that
        // defaulted to it would register every untouched answer as a boy.
        if (Picked<Sex>(_sex) is not { } sex)
        {
            problems.Add(Strings.Register_ChooseSex);
            sex = default;
        }

        // Said by the form, in the registrar's language, whatever else is
        // missing: the registry asks for the place only once the child is
        // named in parts, so an empty form would otherwise not mention it.
        if (Picked<PlaceOfBirthKind>(_placeKind) is null)
        {
            problems.Add(Strings.Register_ChoosePlace);
        }

        var plurality = Picked<BirthPlurality>(_plurality) ?? BirthPlurality.Singleton;

        LateRegistrationDetails? late = null;
        if (_late.IsVisible)
        {
            if (Picked<LateRegistrationEvidenceType>(_evidence) is not { } evidence)
            {
                problems.Add(Strings.Register_ChooseEvidence);
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
        if (_withStatistics.IsChecked)
        {
            statistics = new MaternalStatisticsRequest
            {
                MotherEducationLevel = Picked<EducationLevel>(_education),
                PriorLiveBirths = Whole(_priorLive, Strings.Register_WhatPriorLive) ?? 0,
                PrenatalVisitCount = Whole(_prenatal, Strings.Register_WhatPrenatal),
            };
        }

        var placeKind = Picked<PlaceOfBirthKind>(_placeKind);
        var marriage = _married.Value is null && Text(_marriageCertificate) is null
            ? null
            : new MarriageDetails { Date = _married.Value, CertificateNumber = Text(_marriageCertificate) };
        var proof = Text(_proofKind) is null && Text(_proofReference) is null
            ? null
            : new ProofOfAddressDetails { Kind = Text(_proofKind), Reference = Text(_proofReference) };

        birth = new RegisterBirthRequest
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
            Sex = sex,
            Plurality = plurality,
            BirthOrder = plurality == BirthPlurality.Singleton ? null : Whole(_order, Strings.Register_WhatOrder),
            BirthWeightGrams = Whole(_weight, Strings.Register_WhatWeight),
            GestationalAgeWeeks = weeks,
            Mother = _mother.Read(),
            Father = _father.Read(),
            Marriage = marriage,
            ProofOfAddress = proof,
            RegisteredAtUtc = capturedAt,
            LateRegistration = late,
            MaternalStatistics = statistics,
        };

        return problems;
    }

    private Entry[] Entries =>
    [
        _childGiven, _childSurname, _placeWhere, _order, _weight, _gestation, _marriageCertificate, _proofKind, _proofReference,
        _evidenceReference, _declarant, _relationship, _priorLive, _prenatal,
    ];

    private bool HasInput()
        => Entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Text))
           || _mother.HasInput || _father.HasInput || _married.HasInput
           || _sex.SelectedIndex >= 0 || _placeKind.SelectedIndex >= 0 || _evidence.SelectedIndex >= 0 || _withStatistics.IsChecked;

    private void Clear()
    {
        foreach (var entry in Entries)
        {
            entry.Text = "";
        }

        _mother.Clear();
        _father.Clear();
        _married.Set(null);
        _marriage.Open(false);
        _proof.Open(false);
        _sex.SelectedIndex = _placeKind.SelectedIndex = _evidence.SelectedIndex = _education.SelectedIndex = -1;
        _plurality.SelectedIndex = 0;
        _born.Date = DateTime.Today;
        _withStatistics.IsChecked = false;
        Status.Text = "";
    }

    // --- small helpers ------------------------------------------------------------------------------

    private static Picker Choice<T>(string title, IEnumerable<T> values) where T : struct, Enum => Choices.Choice(title, values);

    private static T? Picked<T>(Picker picker) where T : struct, Enum => Choices.Picked<T>(picker);

    private static string? Text(Entry entry) => string.IsNullOrWhiteSpace(entry.Text) ? null : entry.Text.Trim();


    private static VerticalStackLayout Section(string title, params View[] views)
    {
        var section = new VerticalStackLayout { Spacing = Space.Md };
        section.Add(Ui.Heading(title));
        foreach (var view in views)
        {
            section.Add(view is Entry or Picker ? Labeled(view) : view);
        }

        return section;
    }
}
