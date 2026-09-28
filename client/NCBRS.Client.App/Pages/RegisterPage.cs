using System.Globalization;
using System.Text.RegularExpressions;
using NCBRS.Client.App.Services;
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
public sealed partial class RegisterPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly Label _queue = new() { FontSize = 13 };
    private readonly Label _result = new() { FontSize = 16 };

    private readonly Entry _child = Field("Child's full name");
    private readonly DatePicker _born = new() { MaximumDate = DateTime.Today, Date = DateTime.Today, Format = "d MMM yyyy" };
    private readonly Picker _sex = Choice<Sex>("Sex", [Sex.Female, Sex.Male, Sex.Undetermined]);
    private readonly Picker _plurality = Choice<BirthPlurality>("Single or multiple birth", Enum.GetValues<BirthPlurality>());
    private readonly Entry _order = Field("Birth order among the twins or triplets (1, 2, 3…)", keyboard: Keyboard.Numeric);
    private readonly Entry _weight = Field("Birth weight in grams (optional)", keyboard: Keyboard.Numeric);
    private readonly Entry _gestation = Field("Gestational age in weeks (optional)", keyboard: Keyboard.Numeric);
    private readonly Entry _mother = Field("Mother's full name");
    private readonly Entry _father = Field("Father's full name");

    private readonly VerticalStackLayout _late;
    private readonly Label _lateNote = new() { FontSize = 13, TextColor = Colors.DarkRed };
    private readonly Picker _evidence = Choice<LateRegistrationEvidenceType>("Evidence seen", Enum.GetValues<LateRegistrationEvidenceType>());
    private readonly Entry _evidenceReference = Field("Evidence reference, e.g. card number (optional)");
    private readonly Entry _declarant = Field("Declarant's full name");
    private readonly Entry _relationship = Field("Declarant's relationship to the child, e.g. mother");

    private readonly CheckBox _withStatistics = new();
    private readonly VerticalStackLayout _statistics;
    private readonly Picker _education = Choice<EducationLevel>("Mother's education", Enum.GetValues<EducationLevel>());
    private readonly Entry _priorLive = Field("Children born alive before this one", keyboard: Keyboard.Numeric);
    private readonly Entry _prenatal = Field("Antenatal visits (optional)", keyboard: Keyboard.Numeric);

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
        : base(correcting is null ? "Register a birth" : "Correct a refused birth")
    {
        _host = host;
        _correcting = correcting;
        _windowDays = RegistrationRules.WindowStatedIn(reasons ?? []) ?? RegistrationRules.DefaultStatutoryWindowDays;
        _plurality.SelectedIndex = 0;
        _order.IsVisible = false;
        _plurality.SelectedIndexChanged += (_, _) => _order.IsVisible = Picked<BirthPlurality>(_plurality) is not BirthPlurality.Singleton;

        _late = Section("Registered late", _lateNote, _evidence, _evidenceReference, _declarant, _relationship);
        _born.DateSelected += (_, _) => ShowLateSection();
        ShowLateSection();

        _statistics = Section("Statistics (optional, never delays the registration)", _education, _priorLive, _prenatal);
        _statistics.IsVisible = false;
        _withStatistics.CheckedChanged += (_, args) => _statistics.IsVisible = args.Value;

        var register = new Button { Text = "Register this birth" };
        register.Clicked += async (_, _) => await RunAsync(RegisterAsync);
        var sync = new Button { Text = "Sync now" };
        sync.Clicked += async (_, _) => await RunAsync(SyncAsync);
        var lockTablet = new Button { Text = "Lock", BackgroundColor = Colors.Gray };
        lockTablet.Clicked += (_, _) =>
        {
            host.Lock();
            Flow.Advance(host);
        };

        var form = new View[]
        {
            new Label
            {
                Text = "First version of this form, to be reworked with midwives and community health workers.",
                FontSize = 12, FontAttributes = FontAttributes.Italic,
            },
            Caption("The child"), _child, Caption("Date of birth"), _born, _sex, _plurality, _order, _weight, _gestation,
            Caption("The parents"), _mother, _father,
            _late,
            new HorizontalStackLayout { Spacing = 8, Children = { _withStatistics, new Label { Text = "Add maternal statistics", VerticalOptions = LayoutOptions.Center } } },
            _statistics,
            Status, Busy,
        };

        if (correcting is not null)
        {
            // The registry's reasons first, then the birth as it was sent, to put right.
            Fill(correcting.Birth);
            register.Text = "Save the correction";
            var back = new Button { Text = "Back, without saving", BackgroundColor = Colors.Gray };
            back.Clicked += (_, _) => Flow.Show(new RefusedPage(host));
            Build([
                Heading($"Correct {correcting.Birth.Brn}"),
                new Label
                {
                    FontSize = 14, TextColor = Colors.DarkRed,
                    Text = "The registry refused this birth:\n"
                           + string.Join("\n", (reasons ?? []).Select(reason => $"• {Label(reason.Field)}: {reason.Message}")),
                },
                Note($"Its number {correcting.Birth.Brn} and the time it was first entered stay the same. "
                     + "Put right what the registry refused; it is sent again at the next sync."),
                .. form, register, back]);
            return;
        }

        var refused = new Button { BackgroundColor = Colors.DarkRed, IsVisible = false };
        refused.Clicked += (_, _) => Flow.Show(new RefusedPage(host));
        _refusedBanner = refused;

        Build([
            Heading($"Unlocked: {host.UnlockedAs?.DisplayName}"),
            refused,
            .. form, register, _result, _queue, sync, lockTablet]);
        Refresh();
    }

    private Button? _refusedBanner;

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
        _lateNote.Text = $"This birth is more than {_windowDays} days ago. "
                         + "The law asks for evidence and someone to declare it; a district registrar checks them before a certificate is issued.";
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
                .Select(problem => $"{Label(problem.Field)}: {problem.Message}"))
            .ToList();
        if (problems.Count > 0)
        {
            await ShowProblemAsync(string.Join("\n", problems));
            return;
        }

        if (_correcting is not null)
        {
            if (!await DisplayAlertAsync(
                    "Save this correction?",
                    $"{birth.ChildFullName}, born {birth.DateOfBirth:d MMM yyyy}, stays {_correcting.Birth.Brn}. It is sent again at the next sync.",
                    "Save", "Go back"))
            {
                return;
            }

            await _host.CorrectAsync(_correcting.Birth.Brn, birth);
            Flow.Show(new RefusedPage(_host));
            return;
        }

        // Confirmed before a number is used: once on a slip it cannot be taken back.
        if (!await DisplayAlertAsync(
                "Register this birth?",
                $"{birth.ChildFullName}, {Words(birth.Sex.ToString()).ToLowerInvariant()}, born {birth.DateOfBirth:d MMM yyyy}"
                + (birth.MotherFullName is { Length: > 0 } mother ? $", mother {mother}" : "")
                + (birth.LateRegistration is not null ? ". Registered late, with evidence." : "."),
                "Register", "Go back"))
        {
            return;
        }

        var draft = await _host.RegisterAsync(birth);
        _result.Text = draft.IsProvisional
            ? $"PROVISIONAL slip: {draft.Brn}\nA permanent number is given when the tablet syncs."
            : $"Registered — BRN {draft.Brn}";
        if (draft.BlockLow)
        {
            _result.Text += "\nNumbers are running low: sync when there is signal.";
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

            if (int.TryParse(entry.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            problems.Add($"{what} must be a whole number.");
            return null;
        }

        decimal? weeks = null;
        if (!string.IsNullOrWhiteSpace(_gestation.Text))
        {
            if (decimal.TryParse(_gestation.Text.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                weeks = parsed;
            }
            else
            {
                problems.Add("Gestational age must be a number of weeks.");
            }
        }

        // Asked, never assumed: the enum's first value is Male, and a form that
        // defaulted to it would register every untouched answer as a boy.
        if (Picked<Sex>(_sex) is not { } sex)
        {
            problems.Add("Choose the child's sex (Undetermined if it cannot be told).");
            sex = default;
        }

        var plurality = Picked<BirthPlurality>(_plurality) ?? BirthPlurality.Singleton;

        LateRegistrationDetails? late = null;
        if (_late.IsVisible)
        {
            if (Picked<LateRegistrationEvidenceType>(_evidence) is not { } evidence)
            {
                problems.Add("Choose the evidence seen for a late registration.");
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
                PriorLiveBirths = Whole(_priorLive, "Children born alive before") ?? 0,
                PrenatalVisitCount = Whole(_prenatal, "Antenatal visits"),
            };
        }

        birth = new RegisterBirthRequest
        {
            ChildFullName = Text(_child) ?? "",
            DateOfBirth = BornOn,
            Sex = sex,
            Plurality = plurality,
            BirthOrder = plurality == BirthPlurality.Singleton ? null : Whole(_order, "Birth order"),
            BirthWeightGrams = Whole(_weight, "Birth weight"),
            GestationalAgeWeeks = weeks,
            MotherFullName = Text(_mother),
            FatherFullName = Text(_father),
            RegisteredAtUtc = capturedAt,
            LateRegistration = late,
            MaternalStatistics = statistics,
        };

        return problems;
    }

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
        _queue.Text = $"Waiting to sync: {facility?.SendableCount}   ·   numbers left: {facility?.BlockRemaining}";

        // Never out of sight: a refused birth is one the registry does not have.
        var refused = facility?.Refused.Count ?? 0;
        if (_refusedBanner is not null)
        {
            _refusedBanner.IsVisible = refused > 0;
            _refusedBanner.Text = refused == 1
                ? "1 birth was refused by the registry. Tap to correct it."
                : $"{refused} births were refused by the registry. Tap to correct them.";
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
            .Select(outcome => $"\n{outcome.Brn} is now BRN {outcome.AssignedBrn}"));
        var refusals = string.Concat(rejected.Select(outcome =>
            $"\n{outcome.Brn} refused: {string.Join("; ", outcome.Errors?.Select(error => error.Message) ?? [])}"));

        var upload = report.Upload switch
        {
            null => "Nothing waiting to send.",
            CentralOutcome.Succeeded => $"Sent: {settled.Count} registered with the registry"
                                        + (rejected.Count > 0 ? $", {rejected.Count} refused and kept here" : "")
                                        + assigned + refusals,
            CentralOutcome.Held => "Held at the district office. NOT yet confirmed by the registry. Do not tell the family it is registered.",
            CentralOutcome.Unauthorized => "The tablet's sign-in has ended. A registrar must sign in again while there is signal.",
            CentralOutcome.Unreachable => "No connection to the registry. The births are kept and will go next time.",
            _ => $"Upload: {report.Upload}",
        };

        return upload + (report.BlockGranted is { } block ? $"\nNew numbers: {block.BlockStart} to {block.BlockEnd}" : "");
    }

    // --- small helpers ------------------------------------------------------------------------------

    private static Picker Choice<T>(string title, IEnumerable<T> values) where T : struct, Enum
        => new() { Title = title, ItemsSource = values.Select(value => new Option<T>(value)).ToList(), SelectedIndex = -1 };

    private static T? Picked<T>(Picker picker) where T : struct, Enum
        => picker.SelectedItem is Option<T> option ? option.Value : null;

    private sealed record Option<T>(T Value) where T : struct, Enum
    {
        public override string ToString() => Words(Value.ToString());
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    /// <summary>HealthFacilityRecord → "Health facility record".</summary>
    private static string Words(string name)
    {
        var words = WordBoundary().Split(name);
        return string.Join(' ', words.Select((word, i) => i == 0 ? word : word.ToLowerInvariant()));
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

    /// <summary>The registry's field names, as a registrar would say them.</summary>
    private static string Label(string field) => field switch
    {
        "childFullName" => "Child's name",
        "dateOfBirth" => "Date of birth",
        "sex" => "Sex",
        "plurality" => "Single or multiple birth",
        "birthOrder" => "Birth order",
        "birthWeightGrams" => "Birth weight",
        "gestationalAgeWeeks" => "Gestational age",
        "motherFullName" => "Mother's name",
        "fatherFullName" => "Father's name",
        "registeredAtUtc" => "Tablet clock",
        "lateRegistration" => "Late registration",
        _ when field.StartsWith("lateRegistration.") => "Late registration",
        _ => field,
    };
}
