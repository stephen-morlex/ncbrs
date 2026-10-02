namespace NCBRS.Models;

/// <summary>Where the birth happened, relative to the facility registering it.</summary>
public enum PlaceOfBirthKind
{
    ThisFacility,
    OtherHealthFacility,
    Home,
    Elsewhere,
}

/// <summary>An identity document a parent showed. Recorded as type and number only; the paper stays with the family.</summary>
public enum IdentityDocumentType
{
    Passport,
    BirthCertificate,
    DrivingLicence,
    NationalId,
}

/// <summary>
/// What is recorded about a parent. Every part is optional: a registrar takes
/// what the family can give, and a birth is registered without any of it.
///
/// <see cref="MaidenSurname"/> is the mother's only. <see cref="Occupation"/>
/// is free text, as it was said (decided 2026-10-02); the coded ISCO groups in
/// maternal statistics remain what national figures count.
/// </summary>
public record ParentDetails
{
    public string? GivenNames { get; init; }

    public string? Surname { get; init; }

    public string? MaidenSurname { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    public string? PlaceOfBirth { get; init; }

    public string? Occupation { get; init; }

    public string? Address { get; init; }

    public IdentityDocumentType? DocumentType { get; init; }

    public string? DocumentNumber { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasName => !string.IsNullOrWhiteSpace(GivenNames) || !string.IsNullOrWhiteSpace(Surname);
}

/// <summary>
/// The parents' marriage: statutory, customary or religious. South Sudanese
/// law recognises no civil partnership, so there is none to record.
/// </summary>
public record MarriageDetails
{
    public DateOnly? Date { get; init; }

    public string? CertificateNumber { get; init; }
}

/// <summary>A document shown as proof of address, for example a utility bill: what it was and its reference.</summary>
public record ProofOfAddressDetails
{
    public string? Kind { get; init; }

    public string? Reference { get; init; }
}

/// <summary>
/// The fuller registration on a looked-up record. Addresses, document numbers
/// and certificate references are personal data beyond what the record header
/// shows, and the lookup by exact BRN is open to any signed-in caller (a family
/// carries the number between facilities), so those parts are present only for
/// a caller who may act for the record's facility; <see cref="Restricted"/>
/// says when they were withheld.
/// </summary>
public record RegistrationDetails(
    string? ChildGivenNames,
    string? ChildSurname,
    PlaceOfBirthKind? PlaceOfBirthKind,
    string? PlaceOfBirth,
    ParentDetails? Mother,
    ParentDetails? Father,
    MarriageDetails? Marriage,
    ProofOfAddressDetails? ProofOfAddress,
    bool Restricted);

/// <summary>
/// One person's full name from its parts. The full name is what the signed
/// certificate, duplicate matching, search and corrections use, so it is
/// always composed the same way, by this, wherever the parts are entered.
/// </summary>
public static class PersonNames
{
    public static string Compose(string? givenNames, string? surname)
        => string.Join(' ', new[] { givenNames, surname }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => string.Join(' ', part!.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
}
