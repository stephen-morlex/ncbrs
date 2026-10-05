using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Corrections to the fuller registration's fields (2026-10-04). What these
/// pin: every one waits for a second registrar; the child's name parts
/// recompose the full name and count as certificate fields; "" clears an
/// optional field; a correction that would leave the record describing
/// someone it did not mean to is refused before anything is stored; the
/// event says which fields changed but carries none of their values; and the
/// history withholds addresses and document numbers from another facility.
/// </summary>
public class FullerCorrectionTests : IDisposable
{
    private static readonly Guid Juba = Guid.CreateVersion7();
    private static readonly Guid Terekeka = Guid.CreateVersion7();
    private static readonly Guid NurseId = Guid.CreateVersion7();
    private static readonly Guid OfficerId = Guid.CreateVersion7();
    private const string OfficerSubject = "juba-officer";
    private const string OtherSubject = "terekeka-nurse";

    private readonly TestDatabase _database = TestDatabase.Create();
    private readonly NoOpEventPublisher _events = new();

    public FullerCorrectionTests()
    {
        using var db = NewDb();
        db.Facilities.AddRange(
            new Facility { FacilityId = Juba, Name = "Rejaf PHCU", CountyCode = "SS0101", BrnBlockStart = 100_000, BrnBlockEnd = 199_999, BrnBlockNextAvailable = 100_100 },
            new Facility { FacilityId = Terekeka, Name = "Tali PHCU", CountyCode = "SS0105", BrnBlockStart = 200_000, BrnBlockEnd = 299_999 });
        db.Registrars.AddRange(
            new Registrar { RegistrarId = NurseId, FacilityId = Juba, ExternalSubjectId = AuthTestContext.DefaultSubject, DisplayName = "Nurse Lado" },
            new Registrar { RegistrarId = OfficerId, FacilityId = Juba, ExternalSubjectId = OfficerSubject, DisplayName = "Officer Wani", Role = RegistrarRole.DistrictOfficer },
            new Registrar { FacilityId = Terekeka, ExternalSubjectId = OtherSubject, DisplayName = "Nurse Ayen" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static RegisterBirthRequest Full(string brn) => new()
    {
        Brn = brn, FacilityId = Juba, DeviceId = "web",
        ChildGivenNames = "Ayen Akol", ChildSurname = "Deng",
        DateOfBirth = DateTime.UtcNow.Date.AddDays(-2), Sex = Sex.Female, Plurality = BirthPlurality.Singleton,
        PlaceOfBirthKind = PlaceOfBirthKind.Home, PlaceOfBirth = "Gumbo",
        Mother = new ParentDetails { GivenNames = "Achol", Surname = "Deng", Occupation = "Teacher", Address = "Gumbo, Juba" },
        RegisteredAtUtc = DateTime.UtcNow,
    };

    private async Task RegisterAsync(RegisterBirthRequest request)
    {
        await using var db = NewDb();
        var current = AuthTestContext.RegistrarService(db, AuthTestContext.HttpContextFor());
        var nurse = await db.Registrars.SingleAsync(entry => entry.RegistrarId == NurseId);
        var counties = new CountyLookup(db);
        var result = await new BirthRegistrationService(db, new NoOpEventPublisher(), current,
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
                    NullLogger<DuplicateDetectionService>.Instance, counties),
                counties, Options.Create(new StatutoryRegistrationOptions()))
            .RegisterAsync(request, nurse, Guid.CreateVersion7());
        Assert.Equal(RegistrationOutcome.Registered, result.Outcome);
    }

    private AmendmentService Amendments(NcbrsDbContext db, HttpContext http)
        => new(db, _events, new CertificateRevocationRecorder(db), AuthTestContext.RegistrarService(db, http), new CountyLookup(db));

    /// <summary>Submitted by the nurse, then -- if it was accepted for review -- approved by the officer.</summary>
    private async Task<AmendmentOutcome> CorrectAsync(string brn, AmendBirthRecordRequest request, bool approve = true)
    {
        AmendmentOutcome outcome;
        await using (var db = NewDb())
        {
            var nurse = await db.Registrars.SingleAsync(entry => entry.RegistrarId == NurseId);
            outcome = await Amendments(db, AuthTestContext.HttpContextFor())
                .AmendAsync(brn, request with { Reason = "Corrected from the antenatal card.", DeviceId = "web" }, nurse, Guid.CreateVersion7());
        }

        if (approve && outcome.Succeeded && outcome.Response!.PendingApproval.Count > 0)
        {
            await using var db = NewDb();
            var officer = await db.Registrars.SingleAsync(entry => entry.RegistrarId == OfficerId);
            var review = await Amendments(db, AuthTestContext.HttpContextFor(OfficerSubject, NcbrsRoles.DistrictOfficer))
                .ReviewAsync(outcome.Response.AmendmentRequestId, approve: true, officer, "Seen the card.", Guid.CreateVersion7());
            Assert.True(review.Succeeded, review.Detail);
        }

        return outcome;
    }

    private async Task<BirthRecord> RecordAsync(string brn)
    {
        await using var db = NewDb();
        return await db.BirthRecords.Include(entry => entry.ChildPerson).Include(entry => entry.MotherPerson)
            .Include(entry => entry.FatherPerson).SingleAsync(entry => entry.Brn == brn);
    }

    [Fact]
    public async Task EveryNewFieldWaitsForASecondRegistrar()
    {
        await RegisterAsync(Full("100001"));

        var outcome = await CorrectAsync("100001", new AmendBirthRecordRequest
        {
            ChildSurname = "Deeng",
            PlaceOfBirthKind = PlaceOfBirthKind.OtherHealthFacility,
            PlaceOfBirth = "Gumbo PHCU",
            Mother = new ParentDetails { Occupation = "Nurse", MaidenSurname = "Garang" },
            Marriage = new MarriageDetails { Date = new DateOnly(2023, 1, 14) },
            ProofOfAddress = new ProofOfAddressDetails { Kind = "Utility bill" },
        }, approve: false);

        Assert.True(outcome.EverythingPending);
        Assert.Equal(7, outcome.Response!.PendingApproval.Count);
        Assert.Equal("Deng", (await RecordAsync("100001")).ChildPerson!.Surname);
    }

    [Fact]
    public async Task ApprovedChildPartsRecomposeTheFullName()
    {
        await RegisterAsync(Full("100001"));

        await CorrectAsync("100001", new AmendBirthRecordRequest { ChildGivenNames = "Ayen  Akuol", ChildSurname = "Deeng" });

        var child = (await RecordAsync("100001")).ChildPerson!;
        Assert.Equal("Ayen Akuol", child.GivenNames);
        Assert.Equal("Deeng", child.Surname);
        Assert.Equal("Ayen Akuol Deeng", child.FullName);
    }

    [Fact]
    public async Task ApprovedDetailsAreWritten_AndAnEmptyValueClearsOne()
    {
        await RegisterAsync(Full("100001"));

        await CorrectAsync("100001", new AmendBirthRecordRequest
        {
            PlaceOfBirthKind = PlaceOfBirthKind.ThisFacility,
            PlaceOfBirth = "",
            Mother = new ParentDetails
            {
                Occupation = "Nurse", Address = "", DocumentType = IdentityDocumentType.NationalId,
                DocumentNumber = "SS1234567", DateOfBirth = new DateOnly(2001, 4, 2),
            },
            Marriage = new MarriageDetails { Date = new DateOnly(2023, 1, 14), CertificateNumber = "M-22/2023" },
        });

        var record = await RecordAsync("100001");
        Assert.Equal(PlaceOfBirthKind.ThisFacility, record.PlaceOfBirthKind);
        Assert.Null(record.PlaceOfBirth);
        Assert.Equal("Nurse", record.MotherPerson!.Occupation);
        Assert.Null(record.MotherPerson.Address);
        Assert.Equal(IdentityDocumentType.NationalId, record.MotherPerson.IdentityDocumentType);
        Assert.Equal(new DateOnly(2001, 4, 2), record.MotherPerson.DateOfBirth);
        Assert.Equal(new DateOnly(2023, 1, 14), record.ParentsMarriageDate);
        Assert.Equal("M-22/2023", record.MarriageCertificateNumber);
        Assert.Equal("Achol Deng", record.MotherPerson.FullName);
    }

    [Fact]
    public async Task AFatherCanBeAddedByACorrection_ByName()
    {
        await RegisterAsync(Full("100001"));

        await CorrectAsync("100001", new AmendBirthRecordRequest
        {
            Father = new ParentDetails { GivenNames = "Deng", Surname = "Garang", Occupation = "Cattle keeper" },
        });

        var father = (await RecordAsync("100001")).FatherPerson!;
        Assert.Equal("Deng Garang", father.FullName);
        Assert.Equal("Cattle keeper", father.Occupation);
    }

    public static TheoryData<string, AmendBirthRecordRequest> IncompleteCorrections => new()
    {
        { "details for a father who is not recorded, with no name", new() { Father = new ParentDetails { Occupation = "Trader" } } },
        { "the mother's name removed", new() { Mother = new ParentDetails { GivenNames = "", Surname = "" } } },
        { "a birth moved away from the facility with no place", new() { PlaceOfBirthKind = PlaceOfBirthKind.Elsewhere, PlaceOfBirth = "" } },
    };

    [Theory]
    [MemberData(nameof(IncompleteCorrections))]
    public async Task ACorrectionThatWouldLeaveTheRecordWrongIsRefused_AndNothingStored(string why, AmendBirthRecordRequest request)
    {
        await RegisterAsync(Full("100001"));

        var outcome = await CorrectAsync("100001", request, approve: false);

        Assert.True(outcome.Result == AmendmentResult.Incomplete, why);
        await using var db = NewDb();
        Assert.False(await db.BirthRecordAmendments.AnyAsync());
    }

    /// <summary>
    /// A name registered whole has no parts. Correcting one part would compose
    /// a full name from that part alone and drop the rest.
    /// </summary>
    [Fact]
    public async Task OnePartOfANameRegisteredWholeIsRefused_BothParts_Accepted()
    {
        await RegisterAsync(new RegisterBirthRequest
        {
            Brn = "100002", FacilityId = Juba, DeviceId = "web", ChildFullName = "Garang Deng",
            DateOfBirth = DateTime.UtcNow.Date.AddDays(-1), Sex = Sex.Male, Plurality = BirthPlurality.Singleton,
            RegisteredAtUtc = DateTime.UtcNow,
        });

        Assert.Equal(AmendmentResult.Incomplete,
            (await CorrectAsync("100002", new AmendBirthRecordRequest { ChildSurname = "Deeng" }, approve: false)).Result);

        await CorrectAsync("100002", new AmendBirthRecordRequest { ChildGivenNames = "Garang", ChildSurname = "Deeng" });

        Assert.Equal("Garang Deeng", (await RecordAsync("100002")).ChildPerson!.FullName);
    }

    /// <summary>The event says which of these fields changed; the values stay in the register.</summary>
    [Fact]
    public async Task TheEventNamesTheNewFieldsButCarriesNoneOfTheirValues()
    {
        await RegisterAsync(Full("100001"));

        await CorrectAsync("100001", new AmendBirthRecordRequest
        {
            ChildSurname = "Deeng",
            Mother = new ParentDetails { Address = "Munuki, Juba", DocumentType = IdentityDocumentType.Passport, DocumentNumber = "P123" },
            BirthWeightGrams = 3100,
        });

        var changes = _events.Amendments.SelectMany(evt => evt.Changes).ToList();
        Assert.Contains(changes, change => change.Field == "Mother.Address" && change.NewValue is null && change.PreviousValue is null);
        Assert.Contains(changes, change => change.Field == "Mother.DocumentNumber" && change.NewValue is null);
        Assert.Contains(changes, change => change.Field == "ChildSurname" && change.NewValue is null);
        // The fields that always travelled still do.
        Assert.Contains(changes, change => change.Field == "BirthWeightGrams" && change.NewValue == "3100");
    }

    [Fact]
    public async Task ChangingTheChildsNamePartsWithdrawsTheCertificate()
    {
        await RegisterAsync(Full("100001"));
        await using (var db = NewDb())
        {
            var record = await db.BirthRecords.SingleAsync();
            db.Certificates.Add(new Certificate
            {
                BirthRecordId = record.BirthRecordId, IssueDateUtc = DateTime.UtcNow, QrPayload = "x", SignatureHash = "sig",
            });
            await db.SaveChangesAsync();
        }

        await CorrectAsync("100001", new AmendBirthRecordRequest { ChildSurname = "Deeng" });

        await using var check = NewDb();
        Assert.NotNull((await check.Certificates.SingleAsync()).WithdrawnAtUtc);
    }

    // --- the history ---------------------------------------------------------------

    private async Task<IReadOnlyList<AmendmentHistoryEntry>> HistoryAsync(HttpContext http)
    {
        await using var db = NewDb();
        var current = AuthTestContext.RegistrarService(db, http);
        var counties = new CountyLookup(db);
        var controller = new BirthRecordsController(
            db,
            new BirthRegistrationService(db, new NoOpEventPublisher(), current,
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
                    NullLogger<DuplicateDetectionService>.Instance, counties),
                counties, Options.Create(new StatutoryRegistrationOptions())),
            new AmendmentService(db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current, counties),
            current, counties, Options.Create(new StatutoryRegistrationOptions()), AuthTestContext.ChannelGate(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        return (await controller.GetAmendments("100001")).Value!;
    }

    /// <summary>The history is open to any signed-in caller; a corrected address is not.</summary>
    [Fact]
    public async Task TheHistoryWithholdsAnAddressFromAnotherFacility_NotFromThisOne()
    {
        await RegisterAsync(Full("100001"));
        await CorrectAsync("100001", new AmendBirthRecordRequest
        {
            Mother = new ParentDetails { Address = "Munuki, Juba", Occupation = "Nurse" },
        });

        var elsewhere = await HistoryAsync(AuthTestContext.HttpContextFor(OtherSubject, NcbrsRoles.FacilityRegistrar));
        var address = Assert.Single(elsewhere, entry => entry.Field == "Mother.Address");
        Assert.True(address.Withheld);
        Assert.Null(address.NewValue);
        Assert.Null(address.PreviousValue);
        Assert.Equal("Nurse", Assert.Single(elsewhere, entry => entry.Field == "Mother.Occupation").NewValue);

        var here = await HistoryAsync(AuthTestContext.HttpContextFor());
        Assert.Equal("Munuki, Juba", Assert.Single(here, entry => entry.Field == "Mother.Address").NewValue);
    }

    // --- the request's own shape ---------------------------------------------------

    [Theory]
    [InlineData("childFullName with parts", "ChildFullName")]
    [InlineData("father's maiden surname", "father.maidenSurname")]
    [InlineData("motherFullName with parts", "motherFullName")]
    [InlineData("child's given names emptied", "ChildGivenNames")]
    public void TheRequestItselfIsChecked(string why, string field)
    {
        var request = new AmendBirthRecordRequest { Reason = "Corrected from the card.", DeviceId = "web" } with
        {
            ChildFullName = why == "childFullName with parts" ? "Ayen Deng" : null,
            ChildGivenNames = why switch { "childFullName with parts" => "Ayen", "child's given names emptied" => "", _ => null },
            Father = why == "father's maiden surname" ? new ParentDetails { MaidenSurname = "Garang" } : null,
            MotherFullName = why == "motherFullName with parts" ? "Achol Deng" : null,
            Mother = why == "motherFullName with parts" ? new ParentDetails { GivenNames = "Achol" } : null,
        };

        var result = new AmendBirthRecordRequestValidator().Validate(request);

        Assert.Contains(result.Errors, error => string.Equals(error.PropertyName, field, StringComparison.OrdinalIgnoreCase));
    }
}
