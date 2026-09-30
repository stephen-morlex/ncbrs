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
    private readonly Label _queue = new() { FontSize = 13 };
    private readonly Label _result = new() { FontSize = 16 };

    private readonly Entry _child = Field(Strings.Register_ChildName);
    private readonly DatePicker _born = new() { MaximumDate = DateTime.Today, Date = DateTime.Today, Format = "d MMM yyyy" };
    private readonly Picker _sex = Choice<Sex>(Strings.Register_Sex, [Sex.Female, Sex.Male, Sex.Undetermined]);
    private readonly Picker _plurality = Choice<BirthPlurality>(Strings.Register_Plurality, Enum.GetValues<BirthPlurality>());
    private readonly Entry _order = Field(Strings.Register_Order, keyboard: Keyboard.Numeric);
    private readonly Entry _weight = Field(Strings.Register_Weight, keyboard: Keyboard.Numeric);
    private readonly Entry _gestation = Field(Strings.Register_Gestation, keyboard: Keyboard.Numeric);
    private readonly Entry _mother = Field(Strings.Register_Mother);
    private readonly Entry _father = Field(Strings.Register_Father);

    private readonly VerticalStackLayout _late;
    private readonly Label _lateNote = new() { FontSize = 13, TextColor = Colors.DarkRed };
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

        _late = Section(Strings.Register_Late, _lateNote, _evidence, _evidenceReference, _declarant, _relationship);
        _born.DateSelected += (_, _) => ShowLateSection();
        ShowLateSection();

        _statistics = Section(Strings.Register_Statistics, _education, _priorLive, _prenatal);
        _statistics.IsVisible = false;
        _withStatistics.CheckedChanged += (_, args) => _statistics.IsVisible = args.Value;

        var register = new Button { Text = Strings.Register_Submit };
        register.Clicked += async (_, _) => await RunAsync(RegisterAsync);
        var sync = new Button { Text = Strings.Register_Sync };
        sync.Clicked += async (_, _) => await RunAsync(SyncAsync);
        var export = new Button { Text = Strings.Register_Export, BackgroundColor = Colors.DarkSlateGray };
        export.Clicked += async (_, _) => await RunAsync(ExportAsync);
        var lockTablet = new Button { Text = Strings.Register_Lock, BackgroundColor = Colors.Gray };
        lockTablet.Clicked += (_, _) =>
        {
            host.Lock();
            Flow.Advance(host);
        };

        var form = new View[]
        {
            new Label
            {
                Text = Strings.Register_FirstCut,
                FontSize = 12, FontAttributes = FontAttributes.Italic,
            },
            Caption(Strings.Register_Child), _child, Caption(Strings.Register_DateOfBirth), _born, _sex, _plurality, _order, _weight, _gestation,
            Caption(Strings.Register_Parents), _mother, _father,
            _late,
            new HorizontalStackLayout { Spacing = 8, Children = { _withStatistics, new Label { Text = Strings.Register_AddStatistics, VerticalOptions = LayoutOptions.Center } } },
            _statistics,
            Status, Busy,
        };

        if (correcting is not null)
        {
            // The registry's reasons first, then the birth as it was sent, to put right.
            Fill(correcting.Birth);
            register.Text = Strings.Register_SaveCorrection;
            var back = new Button { Text = Strings.Register_BackNoSave, BackgroundColor = Colors.Gray };
            back.Clicked += (_, _) => Flow.Show(new RefusedPage(host));
            Build([
                Heading(Language.Format(Strings.Register_CorrectHeading, correcting.Birth.Brn)),
                new Label
                {
                    FontSize = 14, TextColor = Colors.DarkRed,
                    // The field in the registrar's language; the reason in the registry's words.
                    Text = Strings.Register_RefusedBecause + "\n"
                           + string.Join("\n", (reasons ?? []).Select(reason => $"• {Language.Field(reason.Field)}: {reason.Message}")),
                },
                Note(Language.Format(Strings.Register_CorrectNote, correcting.Birth.Brn)),
                .. form, register, back]);
            return;
        }

        var refused = new Button { BackgroundColor = Colors.DarkRed, IsVisible = false };
        refused.Clicked += (_, _) => Flow.Show(new RefusedPage(host));
        _refusedBanner = refused;

        // Switching rebuilds the page, so a half-typed birth would be lost: ask first.
        var language = Flow.LanguageSwitch(host, async () =>
            !HasInput() || await DisplayAlertAsync(Strings.Language_Switch, Strings.Register_LanguageClears,
                Strings.Language_Switch, Strings.Common_Cancel));

        // Leaving the form clears it, so a half-typed birth asks first.
        var checkCertificate = new Button { Text = Strings.Register_Check, BackgroundColor = Colors.DarkSlateGray };
        checkCertificate.Clicked += async (_, _) =>
        {
            if (!HasInput() || await DisplayAlertAsync(Strings.Check_Title, Strings.Register_LeaveClears,
                    Strings.Register_Check, Strings.Common_Cancel))
            {
                Flow.Show(new CheckCertificatePage(host));
            }
        };

        Build([
            language,
            Heading(Language.Format(Strings.Register_Unlocked, host.UnlockedAs?.DisplayName)),
            refused,
            .. form, register, _result, _queue, sync, export, _exported, checkCertificate, lockTablet]);
        Refresh();
    }

    private Button? _refusedBanner;

    private readonly Label _exported = new() { FontSize = 13 };

    /// <summary>The form, filled from a birth as it was sent.</summary>
    private void Fill(RegisterBirthRequest birth)
    {
        _child.Text = birth.ChildFullName;
        _born.Date = birth.DateOfBirth.Date;
        Select(_sex, birth.Sex);
        Select(_plurality, birth.Plurality);
        _order.Text = birth.BirthOrder?.ToString(CultureInfo.InvariantCulture);
        _weight.Text = birth.BirthWeightGrams?.ToString(CultureInfo.InvariantCulture);
        _gestation.Text = birth.GestationalAgeWeeks?.ToString(CultureInfo.InvariantCulture);
        _mother.Text = birth.MotherFullName;
        _father.Text = birth.FatherFullName;
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

    private static void Select<T>(Picker picker, T value) where T : struct, Enum
        => picker.SelectedIndex = picker.ItemsSource.Cast<Option<T>>().ToList().FindIndex(option => option.Value.Equals(value));

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
                    Language.Format(Strings.Register_ConfirmCorrectionBody, birth.ChildFullName, birth.DateOfBirth, _correcting.Birth.Brn),
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
                Language.Format(Strings.Register_ConfirmBody, birth.ChildFullName, Language.Name(birth.Sex), birth.DateOfBirth)
                + (birth.MotherFullName is { Length: > 0 } mother ? Language.Format(Strings.Register_ConfirmMother, mother) : "")
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

        Clear();
        Refresh();
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

        birth = new RegisterBirthRequest
        {
            ChildFullName = Text(_child) ?? "",
            DateOfBirth = BornOn,
            Sex = sex,
            Plurality = plurality,
            BirthOrder = plurality == BirthPlurality.Singleton ? null : Whole(_order, Strings.Register_WhatOrder),
            BirthWeightGrams = Whole(_weight, Strings.Register_WhatWeight),
            GestationalAgeWeeks = weeks,
            MotherFullName = Text(_mother),
            FatherFullName = Text(_father),
            RegisteredAtUtc = capturedAt,
            LateRegistration = late,
            MaternalStatistics = statistics,
        };

        return problems;
    }

    private bool HasInput()
        => new[] { _child, _order, _weight, _gestation, _mother, _father, _evidenceReference, _declarant, _relationship, _priorLive, _prenatal }
               .Any(entry => !string.IsNullOrWhiteSpace(entry.Text))
           || _sex.SelectedIndex >= 0 || _evidence.SelectedIndex >= 0 || _withStatistics.IsChecked;

    private void Clear()
    {
        foreach (var entry in new[] { _child, _order, _weight, _gestation, _mother, _father, _evidenceReference, _declarant, _relationship, _priorLive, _prenatal })
        {
            entry.Text = "";
        }

        _sex.SelectedIndex = _evidence.SelectedIndex = _education.SelectedIndex = -1;
        _plurality.SelectedIndex = 0;
        _born.Date = DateTime.Today;
        _withStatistics.IsChecked = false;
        Status.Text = "";
    }

    /// <summary>
    /// Seal what is waiting to sync and hand it to the share sheet, from where
    /// the registrar saves it to a USB stick or card. Sealed to the registry:
    /// whoever carries or finds the stick reads nobody's details.
    /// </summary>
    private async Task ExportAsync()
    {
        var (path, count, problem) = await _host.ExportAsync();
        if (problem is not null)
        {
            await ShowProblemAsync(problem);
            return;
        }

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = Language.Format(Strings.Export_ShareTitle, count),
            File = new ShareFile(path!, "application/json"),
        });

        _result.Text = Language.Format(Strings.Export_Done, count);
        Refresh();
    }

    private async Task SyncAsync()
    {
        var (report, staff) = await _host.SyncAsync();
        _result.Text = Describe(report);
        Status.Text = string.Join("\n", report.Problems.Concat(staff));
        Refresh();
    }

    private void Refresh()
    {
        var facility = _host.Session?.Facility;
        _queue.Text = Language.Format(Strings.Register_Queue, facility?.SendableCount, facility?.BlockRemaining);

        // A stick is not a confirmation: say how many on the last one the
        // registry has not yet confirmed, so nobody takes it for registered.
        var unconfirmed = _host.ExportedAndUnconfirmed;
        _exported.IsVisible = unconfirmed > 0;
        _exported.Text = Language.Format(Strings.Register_ExportedUnconfirmed, unconfirmed, _host.State.LastExport?.AtUtc.ToLocalTime());

        // Never out of sight: a refused birth is one the registry does not have.
        var refused = facility?.Refused.Count ?? 0;
        if (_refusedBanner is not null)
        {
            _refusedBanner.IsVisible = refused > 0;
            _refusedBanner.Text = refused == 1
                ? Strings.Register_RefusedOne
                : Language.Format(Strings.Register_RefusedMany, refused);
        }
    }

    /// <summary>
    /// Held is never shown as confirmed: a District node has the births, the
    /// registry has not seen them, and a family must not be told otherwise.
    /// </summary>
    private static string Describe(WindowReport report)
    {
        var settled = report.Settlements.SelectMany(settlement => settlement.Settled).ToList();
        var rejected = report.Settlements.SelectMany(settlement => settlement.Rejected).ToList();
        var assigned = string.Concat(settled
            .Where(outcome => outcome.AssignedBrn is not null)
            .Select(outcome => "\n" + Language.Format(Strings.Sync_Assigned, outcome.Brn, outcome.AssignedBrn)));
        var refusals = string.Concat(rejected.Select(outcome =>
            "\n" + Language.Format(Strings.Sync_Refused, outcome.Brn,
                string.Join("; ", outcome.Errors?.Select(error => error.Message) ?? []))));

        var upload = report.Upload switch
        {
            null => Strings.Sync_Nothing,
            CentralOutcome.Succeeded => Language.Format(Strings.Sync_Sent, settled.Count)
                                        + (rejected.Count > 0 ? Language.Format(Strings.Sync_SentRefused, rejected.Count) : "")
                                        + assigned + refusals,
            CentralOutcome.Held => Strings.Sync_Held,
            CentralOutcome.Unauthorized => Strings.Sync_Unauthorized,
            CentralOutcome.Unreachable => Strings.Sync_Unreachable,
            _ => Language.Format(Strings.Sync_Other, Language.Name(report.Upload.Value)),
        };

        return upload + (report.BlockGranted is { } block
            ? "\n" + Language.Format(Strings.Sync_NewNumbers, block.BlockStart, block.BlockEnd)
            : "");
    }

    // --- small helpers ------------------------------------------------------------------------------

    private static Picker Choice<T>(string title, IEnumerable<T> values) where T : struct, Enum
        => new() { Title = title, ItemsSource = values.Select(value => new Option<T>(value)).ToList(), SelectedIndex = -1 };

    private static T? Picked<T>(Picker picker) where T : struct, Enum
        => picker.SelectedItem is Option<T> option ? option.Value : null;

    /// <summary>A coded answer, shown in the registrar's language, carrying its code.</summary>
    private sealed record Option<T>(T Value) where T : struct, Enum
    {
        public override string ToString() => Language.Name(Value);
    }

    private static string? Text(Entry entry) => string.IsNullOrWhiteSpace(entry.Text) ? null : entry.Text.Trim();

    private static Label Caption(string text) => new() { Text = text, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) };

    private static VerticalStackLayout Section(string title, params View[] views)
    {
        var section = new VerticalStackLayout { Spacing = 10 };
        section.Add(Caption(title));
        foreach (var view in views)
        {
            section.Add(view);
        }

        return section;
    }
}
