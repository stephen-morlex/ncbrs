using NCBRS.Models;

namespace NCBRS.Client;

/// <summary>
/// What the registry will refuse, checked on the tablet while the family is
/// still in front of the registrar. A record refused at sync time comes back
/// days later, to a post that can no longer ask anyone what the right answer
/// was, so every refusal the tablet can foresee it should foresee.
///
/// These restate the centre's rules — the registration validator and the
/// statutory-window decision in the registration service — and the centre
/// still decides. <c>RegistrationRulesParityTests</c> runs the centre's real
/// validator over the same requests, so the two cannot drift apart unnoticed.
///
/// The form fills the fields here; the BRN, facility and device are the
/// core's, and are not checked.
/// </summary>
public static class RegistrationRules
{
    /// <summary>
    /// The statutory window, as the centre's default sets it. It is set in law
    /// and held as configuration at the centre; a deployment that changes it
    /// must change this with it, or the tablet sends late births as on time
    /// and on-time births as late, and the centre refuses both.
    /// </summary>
    public const int DefaultStatutoryWindowDays = 90;

    /// <summary>
    /// The window the registry applied, read from its refusal of a birth on the
    /// statutory window ("…outside the 30-day statutory window…"). When the
    /// registry has ruled on a birth, its window is the truth, not this
    /// tablet's default: a correction judged by the default would show the
    /// wrong part of the form, or refuse the very evidence the registry asked
    /// for. Null when no refusal names one. <c>LateRegistrationTests</c> reads
    /// it back from the real service's words.
    /// </summary>
    public static int? WindowStatedIn(IEnumerable<ApiError> reasons)
        => reasons
            .Where(reason => reason.Field == "lateRegistration")
            .Select(reason => System.Text.RegularExpressions.Regex.Match(reason.Message, @"(\d+)-day statutory window"))
            .Where(match => match.Success)
            .Select(match => (int?)int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

    /// <summary>How far ahead of the centre's clock a device's clock may be before a date is refused as in the future.</summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromHours(12);

    /// <summary>
    /// Late, as the centre decides it: whole days from the date of birth to the
    /// moment of capture on the device, beyond the window. Measured to capture,
    /// not arrival, so weeks offline do not make a birth late.
    /// </summary>
    public static bool IsLate(DateTime dateOfBirth, DateTime capturedAtUtc, int windowDays = DefaultStatutoryWindowDays)
        => (capturedAtUtc.Date - dateOfBirth.Date).TotalDays > windowDays;

    /// <summary>Every refusal the centre would give, by field, in the centre's words. Empty when it would accept.</summary>
    public static IReadOnlyList<ApiError> Problems(
        RegisterBirthRequest birth, DateTime nowUtc, int windowDays = DefaultStatutoryWindowDays)
        => [.. ShapeProblems(birth, nowUtc), .. WindowProblems(birth, windowDays)];

    /// <summary>What the centre's registration validator refuses: each field on its own.</summary>
    public static IReadOnlyList<ApiError> ShapeProblems(RegisterBirthRequest birth, DateTime nowUtc)
    {
        var problems = new List<ApiError>();
        void Refuse(string field, string message) => problems.Add(new ApiError(field, message));

        var latest = nowUtc.Add(ClockSkewTolerance);

        if (string.IsNullOrWhiteSpace(birth.ChildFullName))
        {
            Refuse("childFullName", "childFullName is required.");
        }
        else if (birth.ChildFullName.Length > 200)
        {
            Refuse("childFullName", "childFullName must be 200 characters or fewer.");
        }

        if (birth.DateOfBirth == default)
        {
            Refuse("dateOfBirth", "dateOfBirth is required.");
        }
        else if (birth.DateOfBirth.ToUniversalTime() > latest)
        {
            Refuse("dateOfBirth", "dateOfBirth cannot be in the future.");
        }

        if (!Enum.IsDefined(birth.Sex))
        {
            Refuse("sex", "sex must be one of: Male, Female, Undetermined.");
        }

        if (!Enum.IsDefined(birth.Plurality))
        {
            Refuse("plurality", "plurality must be one of: Singleton, Twin, Triplet, HigherOrderMultiple.");
        }

        if (birth.BirthWeightGrams is < 200 or > 9999)
        {
            Refuse("birthWeightGrams", "birthWeightGrams must be between 200 and 9999 when supplied.");
        }

        if (birth.GestationalAgeWeeks is < 16m or > 45m)
        {
            Refuse("gestationalAgeWeeks", "gestationalAgeWeeks must be between 16 and 45 when supplied.");
        }

        if (birth.BirthOrder is < 1 or > 10)
        {
            Refuse("birthOrder", "birthOrder must be between 1 and 10 when supplied.");
        }

        if (birth.Plurality != BirthPlurality.Singleton && birth.BirthOrder is null)
        {
            Refuse("birthOrder", "birthOrder is required for a multiple birth.");
        }

        if (birth.MotherFullName?.Length > 200)
        {
            Refuse("motherFullName", "motherFullName must be 200 characters or fewer.");
        }

        if (birth.FatherFullName?.Length > 200)
        {
            Refuse("fatherFullName", "fatherFullName must be 200 characters or fewer.");
        }

        if (birth.RegisteredAtUtc is { } captured && captured.ToUniversalTime() > latest)
        {
            Refuse("registeredAtUtc", "registeredAtUtc cannot be in the future.");
        }

        if (birth.LateRegistration is { } late)
        {
            if (!Enum.IsDefined(late.EvidenceType))
            {
                Refuse("lateRegistration.evidenceType", "lateRegistration.evidenceType is not a recognised evidence type.");
            }

            if (string.IsNullOrWhiteSpace(late.DeclarantName))
            {
                Refuse("lateRegistration.declarantName", "lateRegistration.declarantName is required -- someone must stand behind the claim.");
            }
            else if (late.DeclarantName.Length > 200)
            {
                Refuse("lateRegistration.declarantName", "lateRegistration.declarantName must be 200 characters or fewer.");
            }

            if (string.IsNullOrWhiteSpace(late.DeclarantRelationship))
            {
                Refuse("lateRegistration.declarantRelationship", "lateRegistration.declarantRelationship is required, e.g. mother, father, guardian.");
            }
            else if (late.DeclarantRelationship.Length > 100)
            {
                Refuse("lateRegistration.declarantRelationship", "lateRegistration.declarantRelationship must be 100 characters or fewer.");
            }

            if (late.EvidenceReference?.Length > 200)
            {
                Refuse("lateRegistration.evidenceReference", "lateRegistration.evidenceReference must be 200 characters or fewer.");
            }
        }

        return problems;
    }

    /// <summary>
    /// What the centre's registration service refuses: a capture time before
    /// the birth, and the statutory window in both directions — late needs
    /// evidence, and on-time must not carry it.
    /// </summary>
    public static IReadOnlyList<ApiError> WindowProblems(RegisterBirthRequest birth, int windowDays = DefaultStatutoryWindowDays)
    {
        var problems = new List<ApiError>();
        void Refuse(string field, string message) => problems.Add(new ApiError(field, message));

        if (birth.DateOfBirth != default && birth.RegisteredAtUtc is { } capturedAt)
        {
            if (capturedAt < birth.DateOfBirth)
            {
                Refuse("registeredAtUtc", "registeredAtUtc must fall between the date of birth and now.");
            }
            else if (IsLate(birth.DateOfBirth, capturedAt, windowDays) && birth.LateRegistration is null)
            {
                Refuse("lateRegistration",
                    $"This birth is being registered outside the {windowDays}-day statutory window. "
                    + "Supporting evidence and a declarant are required.");
            }
            else if (!IsLate(birth.DateOfBirth, capturedAt, windowDays) && birth.LateRegistration is not null)
            {
                Refuse("lateRegistration",
                    $"This birth is inside the {windowDays}-day statutory window, so late-registration evidence does not apply.");
            }
        }

        return problems;
    }
}
