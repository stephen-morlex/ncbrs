using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The composed BRN at the registry (decided 2026-10-02): a facility with an
/// office code is granted running numbers for the year, composed under its
/// code; the registry confirms a composed number only if it is this
/// facility's office and was granted that year; numbers from before the format
/// are still confirmed and never renumbered; and an office code is set once.
/// </summary>
public class BrnOfficeCodeTests : IDisposable
{
    private static readonly Guid Juba = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a01");
    private static readonly Guid Tali = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a02");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000011");
    private static readonly int Year = DateTime.UtcNow.Year;

    private readonly TestDatabase _database = TestDatabase.Create();

    public BrnOfficeCodeTests()
    {
        using var db = NewDb();
        db.Facilities.AddRange(
            new Facility
            {
                FacilityId = Juba, Name = "Juba Teaching Hospital", CountyCode = "SS0101", OfficeCode = "JTH",
                BrnBlockStart = 100_000, BrnBlockEnd = 199_999, BrnBlockNextAvailable = 100_010,
            },
            new Facility
            {
                FacilityId = Tali, Name = "Tali PHCU", CountyCode = "SS0101",
                BrnBlockStart = 200_000, BrnBlockEnd = 299_999, BrnBlockNextAvailable = 200_000,
            });
        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId, FacilityId = Juba, ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse Lado", Role = RegistrarRole.DistrictOfficer, CredentialHash = "test",
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private BirthRecordsController Controller(NcbrsDbContext db)
    {
        var http = AuthTestContext.HttpContextFor(AuthTestContext.DefaultSubject, AuthTestContext.WebClient, NcbrsRoles.DistrictOfficer);
        var current = AuthTestContext.RegistrarService(db, http);
        var counties = new CountyLookup(db);
        return new BirthRecordsController(
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
    }

    private async Task<BrnBlockResponse> GrantAsync(Guid facilityId, int size)
    {
        await using var db = NewDb();
        var result = await Controller(db).RequestBrnBlock(facilityId,
            new ApiRequest<BrnBlockRequest> { Data = new BrnBlockRequest { BlockSize = size } });
        return Assert.IsType<BrnBlockResponse>(result.Value);
    }

    private async Task<RegistrationResult> RegisterAsync(string brn, Guid facilityId, string name = "Ayen Deng")
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = await db.Registrars.SingleAsync(entry => entry.RegistrarId == RegistrarId);
        var counties = new CountyLookup(db);
        return await new BirthRegistrationService(db, new NoOpEventPublisher(), current,
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
                    NullLogger<DuplicateDetectionService>.Instance, counties),
                counties, Options.Create(new StatutoryRegistrationOptions()))
            .RegisterAsync(new RegisterBirthRequest
            {
                Brn = brn, FacilityId = facilityId, DeviceId = "web",
                ChildFullName = name, DateOfBirth = DateTime.UtcNow.Date.AddDays(-2),
                Sex = Sex.Female, Plurality = BirthPlurality.Singleton, RegisteredAtUtc = DateTime.UtcNow,
            }, registrar, Guid.CreateVersion7());
    }

    private async Task<BirthRecord> RecordAsync(string brn)
    {
        await using var db = NewDb();
        return await db.BirthRecords.SingleAsync(entry => entry.Brn == brn);
    }

    private async Task<string?> AuditAsync(string brn)
    {
        await using var db = NewDb();
        return await db.AuditLogs.Where(row => row.EntityId == brn && row.Action.StartsWith("BrnUnconfirmed"))
            .Select(row => row.Action).SingleOrDefaultAsync();
    }

    // --- granting ---------------------------------------------------------------

    [Fact]
    public async Task AFacilityWithAnOfficeCode_IsGrantedThisYearsRunningNumbers_Composed()
    {
        var first = await GrantAsync(Juba, 200);
        var second = await GrantAsync(Juba, 50);

        Assert.Equal((1, 200), (first.BlockStart, first.BlockEnd));
        Assert.Equal((201, 250), (second.BlockStart, second.BlockEnd));
        Assert.Equal("JTH", first.OfficeCode);
        Assert.Equal(Year, first.Year);
        Assert.Equal(BrnFormat.Compose("JTH", Year, 1), first.FirstBrn);
        Assert.Equal(BrnFormat.Compose("JTH", Year, 250), second.LastBrn);
    }

    [Fact]
    public async Task AFacilityWithoutOne_KeepsIssuingFromItsNumericRange()
    {
        var grant = await GrantAsync(Tali, 10);

        Assert.Equal((200_000, 200_009), (grant.BlockStart, grant.BlockEnd));
        Assert.Null(grant.OfficeCode);
        Assert.Null(grant.Year);
        Assert.Equal("200000", grant.FirstBrn);
    }

    /// <summary>The same rule as the numeric range: a number already on a record is never granted again.</summary>
    [Fact]
    public async Task AGrantSkipsComposedNumbersAlreadyOnARecord()
    {
        await RegisterAsync(BrnFormat.Compose("JTH", Year, 1), Juba, "Garang Deng");
        await RegisterAsync(BrnFormat.Compose("JTH", Year, 2), Juba, "Achol Lado");

        var grant = await GrantAsync(Juba, 10);

        Assert.Equal(3, grant.BlockStart);
    }

    // --- confirming -------------------------------------------------------------

    [Fact]
    public async Task AGrantedComposedNumberIsConfirmed()
    {
        var grant = await GrantAsync(Juba, 5);

        Assert.True((await RegisterAsync(grant.FirstBrn!, Juba)).Succeeded);

        Assert.NotNull((await RecordAsync(grant.FirstBrn!)).ConfirmedAtUtc);
    }

    /// <summary>Unconfirmable is never refused: the number may already be on a slip in a family's hands.</summary>
    [Theory]
    [InlineData("not granted")]
    [InlineData("another office")]
    [InlineData("mistyped")]
    public async Task AComposedNumberThatCannotBeConfirmed_IsRegisteredUnconfirmed_AndAudited(string why)
    {
        await GrantAsync(Juba, 5);
        var good = BrnFormat.Compose("JTH", Year, 3);
        var (brn, reason) = why switch
        {
            "not granted" => (BrnFormat.Compose("JTH", Year, 600), "NotYetAllocated"),
            "another office" => (BrnFormat.Compose("TKCH", Year, 3), "WrongOffice"),
            _ => (good[..^1] + (good[^1] == 'A' ? 'B' : 'A'), "Mistyped"),
        };

        Assert.True((await RegisterAsync(brn, Juba)).Succeeded);

        Assert.Null((await RecordAsync(brn)).ConfirmedAtUtc);
        Assert.Equal($"BrnUnconfirmed:{reason}", await AuditAsync(brn));
    }

    /// <summary>A number issued before the facility had a code is still its number, and still confirmed.</summary>
    [Fact]
    public async Task ANumericNumberFromBeforeTheCodeIsStillConfirmed()
    {
        Assert.True((await RegisterAsync("100005", Juba)).Succeeded);

        Assert.NotNull((await RecordAsync("100005")).ConfirmedAtUtc);
    }

    [Fact]
    public async Task AProvisionalRecordIsGivenAComposedNumber()
    {
        await RegisterAsync("PROV-TABLET07-3", Juba);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync(entry => entry.ProvisionalIdentifier == "PROV-TABLET07-3");
        var result = await new ProvisionalRecordReconciler(db, new CountyLookup(db)).ReconcileAsync(record, RegistrarId, null);

        Assert.Equal(BrnFormat.Compose("JTH", Year, 1), result.AssignedBrn);
    }

    // --- looking up -------------------------------------------------------------

    [Fact]
    public async Task ALookupReadsTheNumberAsTypedAtACounter()
    {
        var grant = await GrantAsync(Juba, 1);
        await RegisterAsync(grant.FirstBrn!, Juba);

        await using var db = NewDb();
        var typed = " " + grant.FirstBrn!.ToLowerInvariant() + " ";
        var found = (await Controller(db).GetByBrn(typed)).Value;

        Assert.Equal(grant.FirstBrn, found!.Brn);
    }

    /// <summary>"Not found" would send a family away believing their registration lost; this number was misread.</summary>
    [Fact]
    public async Task AMistypedNumberIsSaidToBeMistyped()
    {
        var brn = BrnFormat.Compose("JTH", Year, 7);
        var mistyped = brn[..^1] + (brn[^1] == 'A' ? 'B' : 'A');

        await using var db = NewDb();
        var result = (await Controller(db).GetByBrn(mistyped)).Result;

        var error = Assert.IsType<ApiErrorResponse>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal("This number is mistyped.", error.Title);
    }

    // --- the office code --------------------------------------------------------

    private static FacilityOnboardingService Onboarding(NcbrsDbContext db)
        => new(db, new CountyLookup(db), new FacilityOnboardingOptions());

    private static readonly Registrar Ministry = new() { RegistrarId = Guid.CreateVersion7(), DisplayName = "Ministry", Role = RegistrarRole.MinistryAdmin };

    [Fact]
    public async Task AnOfficeCodeIsGivenOnce_InCapitals_AndAudited()
    {
        await using var db = NewDb();
        var outcome = await Onboarding(db).SetOfficeCodeAsync(Tali, " tali1 ", Ministry, null);

        Assert.Equal(FacilityOnboardingResult.Created, outcome.Result);
        Assert.Equal("TALI1", (await db.Facilities.FindAsync(Tali))!.OfficeCode);
        Assert.True(await db.AuditLogs.AnyAsync(row => row.Action == "OfficeCodeSet:TALI1"));
    }

    /// <summary>Issued numbers carry the code; changing it would leave them naming an office the register no longer knows.</summary>
    [Fact]
    public async Task AnOfficeCodeIsNeverChanged()
    {
        await using var db = NewDb();

        Assert.Equal(FacilityOnboardingResult.AlreadyHasOfficeCode,
            (await Onboarding(db).SetOfficeCodeAsync(Juba, "JTH02", Ministry, null)).Result);
        Assert.Equal(FacilityOnboardingResult.Created,
            (await Onboarding(db).SetOfficeCodeAsync(Juba, "jth", Ministry, null)).Result);
        Assert.Equal("JTH", (await db.Facilities.FindAsync(Juba))!.OfficeCode);
    }

    [Fact]
    public async Task NoTwoFacilitiesShareAnOfficeCode()
    {
        await using var db = NewDb();

        Assert.Equal(FacilityOnboardingResult.OfficeCodeTaken,
            (await Onboarding(db).SetOfficeCodeAsync(Tali, "JTH", Ministry, null)).Result);
    }

    [Fact]
    public void AnOfficeCodeIsTwoToSixLettersOrDigits()
    {
        var validator = new NCBRS.Validation.SetOfficeCodeRequestValidator();

        Assert.True(validator.Validate(new SetOfficeCodeRequest { OfficeCode = "jth01" }).IsValid);
        Assert.False(validator.Validate(new SetOfficeCodeRequest { OfficeCode = "J" }).IsValid);
        Assert.False(validator.Validate(new SetOfficeCodeRequest { OfficeCode = "JTH-01" }).IsValid);
        Assert.False(validator.Validate(new SetOfficeCodeRequest { OfficeCode = "" }).IsValid);
    }

    /// <summary>A coded facility's numbers left are what remains of this year's running numbers.</summary>
    [Fact]
    public async Task AFacilityWithACode_HasThisYearsRunningNumbersLeft()
    {
        await GrantAsync(Juba, 200);

        await using var db = NewDb();
        var juba = (await db.Facilities.FindAsync(Juba))!;
        var next = await new BrnIssuer(db).NextRunningThisYearAsync(juba);

        Assert.Equal(201, next);
        Assert.Equal(BrnFormat.MaxRunning - 200, BrnBlockHealth.Remaining(juba, next));
    }
}
