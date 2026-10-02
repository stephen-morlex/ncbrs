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

        void Required(string field, string? value, int longest)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Refuse(field, $"{field} is required.");
            }
            else if (value.Length > longest)
            {
                Refuse(field, $"{field} must be {longest} characters or fewer.");
            }
        }

        void Longest(string field, string? value, int longest)
        {
            if (value?.Length > longest)
            {
                Refuse(field, $"{field} must be {longest} characters or fewer.");
            }
        }

        if (birth.UsesStructuredNames)
        {
            Required("childGivenNames", birth.ChildGivenNames, 100);
            Required("childSurname", birth.ChildSurname, 100);

            if (!string.IsNullOrWhiteSpace(birth.ChildFullName))
            {
                Refuse("childFullName", "childFullName must be left out when the given names and surname are given.");
            }

            if (birth.PlaceOfBirthKind is null)
            {
                Refuse("placeOfBirthKind", "placeOfBirthKind is required.");
            }
        }
        else
        {
            Required("childFullName", birth.ChildFullName, 200);
        }

        if (birth.PlaceOfBirthKind is { } kind && !Enum.IsDefined(kind))
        {
            Refuse("placeOfBirthKind", "placeOfBirthKind must be one of: ThisFacility, OtherHealthFacility, Home, Elsewhere.");
        }

        if (birth.PlaceOfBirthKind is { } where && where != PlaceOfBirthKind.ThisFacility && string.IsNullOrWhiteSpace(birth.PlaceOfBirth))
        {
            Refuse("placeOfBirth", "placeOfBirth is required when the birth was not at this facility.");
        }

        Longest("placeOfBirth", birth.PlaceOfBirth, 200);

        if (birth.Mother is { } mother)
        {
            ParentProblems(mother, "mother", isMother: true, birth.DateOfBirth, Refuse);
        }

        if (birth.Father is { } father)
        {
            ParentProblems(father, "father", isMother: false, birth.DateOfBirth, Refuse);
        }

        if (birth.Mother?.HasName == true && !string.IsNullOrWhiteSpace(birth.MotherFullName))
        {
            Refuse("motherFullName", "motherFullName must be left out when the mother's given names or surname are given.");
        }

        if (birth.Father?.HasName == true && !string.IsNullOrWhiteSpace(birth.FatherFullName))
        {
            Refuse("fatherFullName", "fatherFullName must be left out when the father's given names or surname are given.");
        }

        if (birth.Marriage is { } marriage)
        {
            if (marriage.Date is { } married && married.ToDateTime(TimeOnly.MinValue) > latest)
            {
                Refuse("marriage.date", "marriage.date cannot be in the future.");
            }

            Longest("marriage.certificateNumber", marriage.CertificateNumber, 50);
        }

        if (birth.ProofOfAddress is { } proof)
        {
            Longest("proofOfAddress.kind", proof.Kind, 100);
            Longest("proofOfAddress.reference", proof.Reference, 100);
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

    /// <summary>The centre's ParentDetailsValidator, restated: one parent's details, every part optional.</summary>
    private static void ParentProblems(ParentDetails parent, string who, bool isMother, DateTime childBorn, Action<string, string> refuse)
    {
        void Longest(string field, string? value, int longest)
        {
            if (value?.Length > longest)
            {
                refuse($"{who}.{field}", $"{who}.{field} must be {longest} characters or fewer.");
            }
        }

        if (!parent.HasName)
        {
            refuse($"{who}.givenNames", $"{who}.givenNames or {who}.surname is required when giving the {who}'s details.");
        }

        Longest("givenNames", parent.GivenNames, 100);
        Longest("surname", parent.Surname, 100);

        if (isMother)
        {
            Longest("maidenSurname", parent.MaidenSurname, 100);
        }
        else if (!string.IsNullOrWhiteSpace(parent.MaidenSurname))
        {
            refuse($"{who}.maidenSurname", $"{who}.maidenSurname applies to the mother only.");
        }

        Longest("placeOfBirth", parent.PlaceOfBirth, 200);
        Longest("occupation", parent.Occupation, 200);
        Longest("address", parent.Address, 300);

        if (parent.DocumentType is { } type && !Enum.IsDefined(type))
        {
            refuse($"{who}.documentType", $"{who}.documentType must be one of: Passport, BirthCertificate, DrivingLicence, NationalId.");
        }

        if (parent.DocumentType is not null && string.IsNullOrWhiteSpace(parent.DocumentNumber))
        {
            refuse($"{who}.documentNumber", $"{who}.documentNumber is required with the document type.");
        }

        if (parent.DocumentType is null && !string.IsNullOrWhiteSpace(parent.DocumentNumber))
        {
            refuse($"{who}.documentType", $"{who}.documentType is required with the document number.");
        }

        Longest("documentNumber", parent.DocumentNumber, 50);

        if (parent.DateOfBirth is { } born && born >= DateOnly.FromDateTime(childBorn))
        {
            refuse($"{who}.dateOfBirth", $"{who}.dateOfBirth must be before the child's date of birth.");
        }
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
