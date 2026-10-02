using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Web;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The fuller registration (2026-10-02): place of birth, the child named in
/// parts, both parents in detail, marriage and documents.
///
/// What these pin: the full name is composed from its parts, so everything
/// that reads it -- the signed certificate, matching, search -- is unchanged;
/// the original one-piece form still registers; the lookup by exact number,
/// open to any signed-in caller, withholds addresses and document numbers from
/// anyone who may not act for the facility; and a correction to a full name
/// does not leave its parts contradicting it.
/// </summary>
public class RegistrationDetailsTests : IDisposable
{
    private static readonly Guid Juba = Guid.CreateVersion7();
    private static readonly Guid Terekeka = Guid.CreateVersion7();
    private static readonly Guid NurseId = Guid.CreateVersion7();
    private const string OtherSubject = "terekeka-nurse";

    private readonly TestDatabase _database = TestDatabase.Create();

    public RegistrationDetailsTests()
    {
        using var db = NewDb();
        db.Facilities.AddRange(
            new Facility { FacilityId = Juba, Name = "Rejaf PHCU", CountyCode = "SS0101", BrnBlockStart = 100_000, BrnBlockEnd = 199_999, BrnBlockNextAvailable = 100_100 },
            new Facility { FacilityId = Terekeka, Name = "Tali PHCU", CountyCode = "SS0105", BrnBlockStart = 200_000, BrnBlockEnd = 299_999 });
        db.Registrars.AddRange(
            new Registrar { RegistrarId = NurseId, FacilityId = Juba, ExternalSubjectId = AuthTestContext.DefaultSubject, DisplayName = "Nurse Lado" },
            new Registrar { FacilityId = Terekeka, ExternalSubjectId = OtherSubject, DisplayName = "Nurse Ayen" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static RegisterBirthRequest Full(string brn = "100001") => new()
    {
        Brn = brn,
        FacilityId = Juba,
        DeviceId = "web",
        ChildGivenNames = "Ayen  Akol",
        ChildSurname = "Deng",
        DateOfBirth = DateTime.UtcNow.Date.AddDays(-2),
        Sex = Sex.Female,
        Plurality = BirthPlurality.Singleton,
        PlaceOfBirthKind = PlaceOfBirthKind.Home,
        PlaceOfBirth = "Gumbo, near the borehole",
        Mother = new ParentDetails
        {
            GivenNames = "Achol", Surname = "Deng", MaidenSurname = "Garang",
            DateOfBirth = new DateOnly(2001, 4, 2), PlaceOfBirth = "Bor", Occupation = "Teacher",
            Address = "Gumbo, Juba", DocumentType = IdentityDocumentType.NationalId, DocumentNumber = "SS1234567",
        },
        Father = new ParentDetails { GivenNames = "Deng", Surname = "Garang", Occupation = "Cattle keeper" },
        Marriage = new MarriageDetails { Date = new DateOnly(2023, 1, 14), CertificateNumber = "M-22/2023" },
        ProofOfAddress = new ProofOfAddressDetails { Kind = "Utility bill", Reference = "JEDCO 7781" },
        RegisteredAtUtc = DateTime.UtcNow,
    };

    private async Task<RegistrationResult> RegisterAsync(RegisterBirthRequest request)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = await db.Registrars.SingleAsync(entry => entry.RegistrarId == NurseId);
        return await new BirthRegistrationService(db, new NoOpEventPublisher(), current,
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
                    NullLogger<DuplicateDetectionService>.Instance, new CountyLookup(db)),
                new CountyLookup(db), Options.Create(new StatutoryRegistrationOptions()))
            .RegisterAsync(request, registrar, Guid.CreateVersion7());
    }

    private async Task<BirthRecordResponse> LookUpAsync(string brn, HttpContext http)
    {
        await using var db = NewDb();
        var current = AuthTestContext.RegistrarService(db, http);
        var districts = new CountyLookup(db);
        var controller = new BirthRecordsController(
            db,
            new BirthRegistrationService(db, new NoOpEventPublisher(), current,
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
                    NullLogger<DuplicateDetectionService>.Instance, districts),
                districts, Options.Create(new StatutoryRegistrationOptions())),
            new AmendmentService(db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current, districts),
            current, districts, Options.Create(new StatutoryRegistrationOptions()), AuthTestContext.ChannelGate(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        return (await controller.GetByBrn(brn)).Value!;
    }

    [Fact]
    public async Task TheFullerFormIsStored_AndTheFullNameComposedFromItsParts()
    {
        Assert.Equal(RegistrationOutcome.Registered, (await RegisterAsync(Full())).Outcome);

        await using var db = NewDb();
        var record = await db.BirthRecords
            .Include(entry => entry.ChildPerson).Include(entry => entry.MotherPerson).Include(entry => entry.FatherPerson)
            .SingleAsync();

        // What the certificate, matching and search read.
        Assert.Equal("Ayen Akol Deng", record.ChildPerson!.FullName);
        Assert.Equal("Ayen Akol", record.ChildPerson.GivenNames);
        Assert.Equal("Deng", record.ChildPerson.Surname);
        Assert.Equal("Achol Deng", record.MotherPerson!.FullName);
        Assert.Equal("Garang", record.MotherPerson.MaidenSurname);
        Assert.Equal(IdentityDocumentType.NationalId, record.MotherPerson.IdentityDocumentType);
        Assert.Equal("Cattle keeper", record.FatherPerson!.Occupation);
        Assert.Equal(PlaceOfBirthKind.Home, record.PlaceOfBirthKind);
        Assert.Equal("Gumbo, near the borehole", record.PlaceOfBirth);
        Assert.Equal(new DateOnly(2023, 1, 14), record.ParentsMarriageDate);
        Assert.Equal("JEDCO 7781", record.ProofOfAddressReference);
    }

    /// <summary>Tablets not yet upgraded send the original form; it still registers, and has no details to show.</summary>
    [Fact]
    public async Task TheOriginalOnePieceFormStillRegisters()
    {
        var original = new RegisterBirthRequest
        {
            Brn = "100002", FacilityId = Juba, DeviceId = "web", ChildFullName = "Garang Deng",
            DateOfBirth = DateTime.UtcNow.Date.AddDays(-1), Sex = Sex.Male, Plurality = BirthPlurality.Singleton,
            MotherFullName = "Achol Deng", RegisteredAtUtc = DateTime.UtcNow,
        };

        Assert.Equal(RegistrationOutcome.Registered, (await RegisterAsync(original)).Outcome);

        var record = await LookUpAsync("100002", AuthTestContext.HttpContextFor());
        Assert.Equal("Garang Deng", record.ChildFullName);
        Assert.Equal("Achol Deng", record.MotherFullName);
        Assert.Null(record.Details);
    }

    [Fact]
    public async Task TheFacilityReadsEverything()
    {
        await RegisterAsync(Full());

        var details = (await LookUpAsync("100001", AuthTestContext.HttpContextFor())).Details!;

        Assert.False(details.Restricted);
        Assert.Equal("Gumbo, Juba", details.Mother!.Address);
        Assert.Equal("SS1234567", details.Mother.DocumentNumber);
        Assert.Equal("M-22/2023", details.Marriage!.CertificateNumber);
        Assert.Equal("JEDCO 7781", details.ProofOfAddress!.Reference);
    }

    /// <summary>
    /// The lookup by exact number is open to any signed-in caller, because a
    /// family carries the number between facilities. Addresses and document
    /// numbers are not: a registrar elsewhere sees that they were withheld.
    /// </summary>
    [Fact]
    public async Task AnotherFacilityIsNotShownAddressesOrDocumentNumbers()
    {
        await RegisterAsync(Full());

        var details = (await LookUpAsync("100001", AuthTestContext.HttpContextFor(OtherSubject, NcbrsRoles.FacilityRegistrar))).Details!;

        Assert.True(details.Restricted);
        Assert.Equal("Achol", details.Mother!.GivenNames);
        Assert.Null(details.Mother.Address);
        Assert.Null(details.Mother.DocumentNumber);
        Assert.Equal(IdentityDocumentType.NationalId, details.Mother.DocumentType);
        Assert.Null(details.Marriage!.CertificateNumber);
        Assert.Null(details.ProofOfAddress!.Reference);
    }

    /// <summary>A correction is to the full name; parts that no longer compose it are cleared, not left contradicting it.</summary>
    [Fact]
    public async Task CorrectingAFullNameClearsItsParts()
    {
        await RegisterAsync(Full());

        await using (var db = NewDb())
        {
            var child = await db.BirthRecords.Include(entry => entry.ChildPerson).Select(entry => entry.ChildPerson!).SingleAsync();
            Assert.Equal("Deng", child.Surname);
        }

        await using (var db = NewDb())
        {
            var http = AuthTestContext.HttpContextFor();
            var current = AuthTestContext.RegistrarService(db, http);
            var nurse = await db.Registrars.SingleAsync(entry => entry.RegistrarId == NurseId);
            var service = new AmendmentService(db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current, new CountyLookup(db));
            var submitted = await service.AmendAsync("100001", new AmendBirthRecordRequest
            {
                MotherFullName = "Achol Garang Deng", Reason = "Mother's name in full.", DeviceId = "web",
            }, nurse, Guid.CreateVersion7());

            // Parents' names wait for a reviewer who is not the submitter.
            var reviewer = new Registrar { FacilityId = Juba, ExternalSubjectId = "reviewer", DisplayName = "Officer", Role = RegistrarRole.DistrictOfficer };
            db.Registrars.Add(reviewer);
            await db.SaveChangesAsync();
            var reviewerHttp = AuthTestContext.HttpContextFor("reviewer", NcbrsRoles.DistrictOfficer);
            var reviewing = new AmendmentService(db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db),
                AuthTestContext.RegistrarService(db, reviewerHttp), new CountyLookup(db));
            Assert.True((await reviewing.ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true, reviewer, "Seen the card.", Guid.CreateVersion7())).Succeeded);
        }

        await using var check = NewDb();
        var mother = await check.BirthRecords.Include(entry => entry.MotherPerson).Select(entry => entry.MotherPerson!).SingleAsync();
        Assert.Equal("Achol Garang Deng", mother.FullName);
        Assert.Null(mother.GivenNames);
        Assert.Null(mother.Surname);
    }
}
