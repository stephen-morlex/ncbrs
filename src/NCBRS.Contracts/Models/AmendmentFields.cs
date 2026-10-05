namespace NCBRS.Models;

/// <summary>
/// The fuller registration's fields as a correction names them, in the
/// amendment history, the review queue and the <c>.amended</c> event: the
/// child's parts by name, a parent's or the family's by a dotted path
/// (<c>Mother.Address</c>, <c>Marriage.Date</c>).
/// </summary>
public static class AmendmentFields
{
    public const string ChildGivenNames = nameof(ChildGivenNames);
    public const string ChildSurname = nameof(ChildSurname);
    public const string PlaceOfBirthKind = nameof(PlaceOfBirthKind);
    public const string PlaceOfBirth = nameof(PlaceOfBirth);

    public const string Mother = nameof(Mother);
    public const string Father = nameof(Father);

    public const string GivenNames = nameof(ParentDetails.GivenNames);
    public const string Surname = nameof(ParentDetails.Surname);
    public const string MaidenSurname = nameof(ParentDetails.MaidenSurname);
    public const string DateOfBirth = nameof(ParentDetails.DateOfBirth);
    public const string ParentPlaceOfBirth = nameof(ParentDetails.PlaceOfBirth);
    public const string Occupation = nameof(ParentDetails.Occupation);
    public const string Address = nameof(ParentDetails.Address);
    public const string DocumentType = nameof(ParentDetails.DocumentType);
    public const string DocumentNumber = nameof(ParentDetails.DocumentNumber);

    public const string MarriageDate = "Marriage.Date";
    public const string MarriageCertificateNumber = "Marriage.CertificateNumber";
    public const string ProofOfAddressKind = "ProofOfAddress.Kind";
    public const string ProofOfAddressReference = "ProofOfAddress.Reference";

    public static string Of(string parent, string part) => $"{parent}.{part}";

    /// <summary>Every field the fuller registration added, as a correction names it.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ChildGivenNames, ChildSurname, PlaceOfBirthKind, PlaceOfBirth,
        .. ParentParts(Mother, includeMaiden: true),
        .. ParentParts(Father, includeMaiden: false),
        MarriageDate, MarriageCertificateNumber, ProofOfAddressKind, ProofOfAddressReference,
    ];

    /// <summary>
    /// Shown only to a caller who may act for the record's facility -- the
    /// same set <see cref="RegistrationDetails.Restricted"/> withholds from the
    /// lookup. Never carried on the event stream at all.
    /// </summary>
    public static IReadOnlySet<string> Withheld { get; } = new HashSet<string>
    {
        Of(Mother, Address), Of(Mother, DocumentNumber),
        Of(Father, Address), Of(Father, DocumentNumber),
        MarriageCertificateNumber, ProofOfAddressReference,
    };

    public static IEnumerable<string> ParentParts(string parent, bool includeMaiden) =>
        new[] { GivenNames, Surname, MaidenSurname, DateOfBirth, ParentPlaceOfBirth, Occupation, Address, DocumentType, DocumentNumber }
            .Where(part => includeMaiden || part != MaidenSurname)
            .Select(part => Of(parent, part));
}
