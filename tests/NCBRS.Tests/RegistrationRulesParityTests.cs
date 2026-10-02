using NCBRS.Client;
using NCBRS.Models;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The tablet's registration form checks what the centre's validator will
/// refuse, so a birth is corrected while the family is still there rather than
/// refused at sync weeks later. This runs the centre's real validator and the
/// tablet's rules over the same requests and requires them to refuse the same
/// fields — a rule changed on one side only fails here.
/// </summary>
public class RegistrationRulesParityTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    /// <summary>A birth both sides accept; each case changes one thing.</summary>
    private static RegisterBirthRequest Valid() => new()
    {
        Brn = "100001",
        FacilityId = Guid.Parse("0199c000-0000-7000-8000-00000000f001"),
        DeviceId = "TAB-0A1B2C3D4E5F",
        ChildFullName = "Ayen Deng",
        DateOfBirth = Now.Date.AddDays(-3),
        Sex = Sex.Female,
        Plurality = BirthPlurality.Singleton,
        BirthWeightGrams = 3100,
        GestationalAgeWeeks = 39m,
        MotherFullName = "Achol Deng",
        FatherFullName = "Deng Garang",
        RegisteredAtUtc = Now,
    };

    /// <summary>The fuller form (2026-10-02): the child named in parts, with where, and both parents in detail.</summary>
    private static RegisterBirthRequest Full() => Valid() with
    {
        ChildFullName = "",
        ChildGivenNames = "Ayen",
        ChildSurname = "Deng",
        PlaceOfBirthKind = PlaceOfBirthKind.Home,
        PlaceOfBirth = "Gumbo, near the borehole",
        MotherFullName = null,
        FatherFullName = null,
        Mother = new ParentDetails
        {
            GivenNames = "Achol", Surname = "Deng", MaidenSurname = "Garang",
            DateOfBirth = DateOnly.FromDateTime(Now.AddYears(-24)), PlaceOfBirth = "Bor",
            Occupation = "Teacher", Address = "Gumbo, Juba", DocumentType = IdentityDocumentType.NationalId, DocumentNumber = "SS1234567",
        },
        Father = new ParentDetails { GivenNames = "Deng", Surname = "Garang", Occupation = "Cattle keeper" },
        Marriage = new MarriageDetails { Date = DateOnly.FromDateTime(Now.AddYears(-3)), CertificateNumber = "M-22/2023" },
        ProofOfAddress = new ProofOfAddressDetails { Kind = "Utility bill", Reference = "JEDCO 7781" },
    };

    private static LateRegistrationDetails Evidence() => new()
    {
        EvidenceType = LateRegistrationEvidenceType.ImmunisationRecord,
        EvidenceReference = "Card 22/2026",
        DeclarantName = "Achol Deng",
        DeclarantRelationship = "mother",
    };

    public static TheoryData<string, RegisterBirthRequest> Cases => new()
    {
        { "valid", Valid() },
        { "no name", Valid() with { ChildFullName = "" } },
        { "long name", Valid() with { ChildFullName = new string('a', 201) } },
        { "no date of birth", Valid() with { DateOfBirth = default } },
        { "born tomorrow", Valid() with { DateOfBirth = Now.AddDays(2) } },
        { "undefined sex", Valid() with { Sex = (Sex)9 } },
        { "undefined plurality", Valid() with { Plurality = (BirthPlurality)9 } },
        { "weight too low", Valid() with { BirthWeightGrams = 199 } },
        { "weight at floor", Valid() with { BirthWeightGrams = 200 } },
        { "weight too high", Valid() with { BirthWeightGrams = 10_000 } },
        { "gestation too short", Valid() with { GestationalAgeWeeks = 15.9m } },
        { "gestation too long", Valid() with { GestationalAgeWeeks = 45.5m } },
        { "birth order zero", Valid() with { BirthOrder = 0 } },
        { "birth order eleven", Valid() with { BirthOrder = 11 } },
        { "twin with no order", Valid() with { Plurality = BirthPlurality.Twin } },
        { "twin with order", Valid() with { Plurality = BirthPlurality.Twin, BirthOrder = 2 } },
        { "long mother", Valid() with { MotherFullName = new string('a', 201) } },
        { "long father", Valid() with { FatherFullName = new string('a', 201) } },
        { "captured in the future", Valid() with { RegisteredAtUtc = Now.AddDays(2) } },
        { "late, well formed", Valid() with { DateOfBirth = Now.Date.AddDays(-200), LateRegistration = Evidence() } },
        { "late, no declarant", Valid() with { DateOfBirth = Now.Date.AddDays(-200), LateRegistration = Evidence() with { DeclarantName = "" } } },
        { "late, no relationship", Valid() with { DateOfBirth = Now.Date.AddDays(-200), LateRegistration = Evidence() with { DeclarantRelationship = " " } } },
        { "late, long relationship", Valid() with { DateOfBirth = Now.Date.AddDays(-200), LateRegistration = Evidence() with { DeclarantRelationship = new string('a', 101) } } },
        { "late, long reference", Valid() with { DateOfBirth = Now.Date.AddDays(-200), LateRegistration = Evidence() with { EvidenceReference = new string('a', 201) } } },
        { "full form, valid", Full() },
        { "full, no given names", Full() with { ChildGivenNames = " " } },
        { "full, no surname", Full() with { ChildSurname = null } },
        { "full, long given names", Full() with { ChildGivenNames = new string('a', 101) } },
        { "full, full name too", Full() with { ChildFullName = "Ayen Deng" } },
        { "full, no place kind", Full() with { PlaceOfBirthKind = null } },
        { "full, undefined place kind", Full() with { PlaceOfBirthKind = (PlaceOfBirthKind)9 } },
        { "home birth, no place", Full() with { PlaceOfBirth = "" } },
        { "this facility, no place", Full() with { PlaceOfBirthKind = PlaceOfBirthKind.ThisFacility, PlaceOfBirth = null } },
        { "long place", Full() with { PlaceOfBirth = new string('a', 201) } },
        { "original form with a place kind", Valid() with { PlaceOfBirthKind = PlaceOfBirthKind.Home, PlaceOfBirth = "Gumbo" } },
        { "original form, home birth, no place", Valid() with { PlaceOfBirthKind = PlaceOfBirthKind.Home } },
        { "mother with no name", Full() with { Mother = new ParentDetails { Address = "Gumbo" } } },
        { "mother, long surname", Full() with { Mother = Full().Mother! with { Surname = new string('a', 101) } } },
        { "mother, long maiden surname", Full() with { Mother = Full().Mother! with { MaidenSurname = new string('a', 101) } } },
        { "father with a maiden surname", Full() with { Father = Full().Father! with { MaidenSurname = "Garang" } } },
        { "mother, long address", Full() with { Mother = Full().Mother! with { Address = new string('a', 301) } } },
        { "mother, long occupation", Full() with { Mother = Full().Mother! with { Occupation = new string('a', 201) } } },
        { "mother, long place of birth", Full() with { Mother = Full().Mother! with { PlaceOfBirth = new string('a', 201) } } },
        { "document type, no number", Full() with { Mother = Full().Mother! with { DocumentNumber = null } } },
        { "document number, no type", Full() with { Mother = Full().Mother! with { DocumentType = null } } },
        { "undefined document type", Full() with { Father = Full().Father! with { DocumentType = (IdentityDocumentType)9, DocumentNumber = "X1" } } },
        { "long document number", Full() with { Mother = Full().Mother! with { DocumentNumber = new string('9', 51) } } },
        { "mother born after the child", Full() with { Mother = Full().Mother! with { DateOfBirth = DateOnly.FromDateTime(Now) } } },
        { "father born after the child", Full() with { Father = Full().Father! with { DateOfBirth = DateOnly.FromDateTime(Now) } } },
        { "mother named twice", Full() with { MotherFullName = "Achol Deng" } },
        { "father named twice", Full() with { FatherFullName = "Deng Garang" } },
        { "married tomorrow", Full() with { Marriage = new MarriageDetails { Date = DateOnly.FromDateTime(Now.AddDays(3)) } } },
        { "long marriage certificate", Full() with { Marriage = new MarriageDetails { CertificateNumber = new string('a', 51) } } },
        { "long proof kind", Full() with { ProofOfAddress = new ProofOfAddressDetails { Kind = new string('a', 101) } } },
        { "long proof reference", Full() with { ProofOfAddress = new ProofOfAddressDetails { Reference = new string('a', 101) } } },
        { "late, undefined evidence", Valid() with { DateOfBirth = Now.Date.AddDays(-200), LateRegistration = Evidence() with { EvidenceType = (LateRegistrationEvidenceType)99 } } },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheTabletRefusesTheFieldsTheCentresValidatorRefuses(string @case, RegisterBirthRequest request)
    {
        var centre = new RegisterBirthRequestValidator().Validate(request).Errors
            .Select(error => Camel(error.PropertyName))
            .ToHashSet();

        // The window and capture-time decisions are the registration
        // service's, not the validator's; they have their own parity test
        // beside the service (LateRegistrationTests).
        var tablet = RegistrationRules.ShapeProblems(request, Now)
            .Select(problem => problem.Field)
            .ToHashSet();

        Assert.True(centre.SetEquals(tablet),
            $"{@case}: centre refused [{string.Join(", ", centre)}], tablet refused [{string.Join(", ", tablet)}]");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheTabletUsesTheCentresWords(string @case, RegisterBirthRequest request)
    {
        var centre = new RegisterBirthRequestValidator().Validate(request).Errors.Select(error => error.ErrorMessage).ToHashSet();
        var tablet = RegistrationRules.ShapeProblems(request, Now)
            .Select(problem => problem.Message)
            .ToHashSet();

        Assert.True(centre.SetEquals(tablet), $"{@case}: [{string.Join(" | ", centre)}] vs [{string.Join(" | ", tablet)}]");
    }

    /// <summary>The cases meant to pass; every other case must be refused, or agreeing proves nothing.</summary>
    private static readonly HashSet<string> Accepted =
    [
        "valid", "weight at floor", "twin with order", "late, well formed",
        "full form, valid", "this facility, no place", "original form with a place kind",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachCaseIsRefusedOrAcceptedAsIntended(string @case, RegisterBirthRequest request)
    {
        var refused = new RegisterBirthRequestValidator().Validate(request).Errors.Count > 0;

        Assert.True(refused != Accepted.Contains(@case), $"{@case}: refused = {refused}");
    }

    private static string Camel(string path)
        => string.Join('.', path.Split('.').Select(part => char.ToLowerInvariant(part[0]) + part[1..]));
}
