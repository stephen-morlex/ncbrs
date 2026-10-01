using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The county rule on the four oversight queues and on duplicate review --
/// the gaps an authorization sweep before the external audit (plan §17 24)
/// found.
///
/// Every review *write* but one already refused another county's records.
/// The exception was duplicate review, which is the one that supersedes a
/// whole registration and withdraws its certificate: a district officer
/// anywhere could confirm a duplicate anywhere. And all four queues were
/// national for reading, so an officer saw other counties' proposed name
/// changes, late-registration evidence and duplicate pairs.
///
/// Two counties, a record in each, and a pair wholly inside each plus one
/// spanning both. A district officer of Juba must see and decide Juba's and
/// nothing else; the Ministry sees and decides everything.
/// </summary>
public class ReviewScopeTests : IDisposable
{
    private static readonly Guid Juba = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid JubaClinic = Guid.Parse("0199a1b2-0001-7000-8000-000000000002");
    private static readonly Guid Terekeka = Guid.Parse("0199a1b2-0001-7000-8000-000000000003");
    private static readonly Guid TerekekaClinic = Guid.Parse("0199a1b2-0001-7000-8000-000000000004");

    private const string OfficerSubject = "officer-juba";
    private const string MinistrySubject = "ministry";

    private readonly TestDatabase _database = TestDatabase.Create();
    private readonly Guid _officer = Guid.CreateVersion7();
    private readonly Guid _ministry = Guid.CreateVersion7();

    // Duplicate pairs: wholly in Juba, wholly in Terekeka, and one spanning both.
    private Guid _jubaPair, _terekekaPair, _crossPair;

    public ReviewScopeTests()
    {
        using var db = NewDb();

        db.Facilities.AddRange(
            Facility(Juba, "Juba Teaching Hospital", "SS0101", 100_000),
            Facility(JubaClinic, "Munuki PHCC", "SS0101", 200_000),
            Facility(Terekeka, "Terekeka County Hospital", "SS0105", 300_000),
            Facility(TerekekaClinic, "Tali PHCU", "SS0105", 400_000));

        db.Registrars.AddRange(
            new Registrar
            {
                RegistrarId = _officer, FacilityId = Juba, ExternalSubjectId = OfficerSubject,
                DisplayName = "District officer, Juba", Role = RegistrarRole.DistrictOfficer, CredentialHash = "x",
            },
            new Registrar
            {
                RegistrarId = _ministry, FacilityId = Juba, ExternalSubjectId = MinistrySubject,
                DisplayName = "Ministry", Role = RegistrarRole.MinistryAdmin, CredentialHash = "x",
            });

        var j1 = Birth("100001", Juba);
        var j2 = Birth("100002", JubaClinic);
        var t1 = Birth("300001", Terekeka);
        var t2 = Birth("300002", TerekekaClinic);
        db.BirthRecords.AddRange(j1, j2, t1, t2);

        _jubaPair = Pair(db, j1, j2);
        _terekekaPair = Pair(db, t1, t2);
        _crossPair = Pair(db, j1, t1);

        foreach (var record in new[] { j1, t1 })
        {
            db.BirthRecordAmendments.Add(new BirthRecordAmendment
            {
                BirthRecordId = record.BirthRecordId, Field = "ChildFullName", PreviousValue = "Ayen Deng",
                NewValue = "Ayen Deeng", Reason = "Misspelled", AmendedByRegistrarId = _officer,
                AmendmentRequestId = Guid.CreateVersion7(), Status = AmendmentStatus.PendingApproval,
            });
            db.AmendmentConflicts.Add(new AmendmentConflict
            {
                BirthRecordId = record.BirthRecordId, AmendmentRequestId = Guid.CreateVersion7(),
                Field = "BirthWeightGrams", ExpectedPreviousValue = "3000", ActualPreviousValue = "3100",
                DetectedFromRegistrarId = _officer,
            });
            db.LateRegistrations.Add(new LateRegistration
            {
                BirthRecordId = record.BirthRecordId, DaysLate = 120, WindowDaysAtFiling = 90,
                EvidenceType = LateRegistrationEvidenceType.AntenatalOrDeliveryCard,
                DeclarantName = "Achol Deng", DeclarantRelationship = "Mother", SubmittedByRegistrarId = _officer,
            });
        }

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    // Each its own range: no two facilities may share one (FacilityRangeStartUnique).
    private static Facility Facility(Guid id, string name, string county, long rangeStart) => new()
    {
        FacilityId = id, Name = name, CountyCode = county,
        BrnBlockStart = rangeStart, BrnBlockEnd = rangeStart + 99_999, BrnBlockNextAvailable = rangeStart,
    };

    private BirthRecord Birth(string brn, Guid facility) => new()
    {
        Brn = brn, FacilityId = facility, ChildPerson = new Person { FullName = $"Child {brn}" },
        DateOfBirth = DateTime.UtcNow.Date.AddDays(-10), Sex = Sex.Female, Plurality = BirthPlurality.Singleton,
        RegisteredByRegistrarId = _ministry,
    };

    private static Guid Pair(NcbrsDbContext db, BirthRecord left, BirthRecord right)
    {
        var link = new DuplicateCandidate
        {
            BirthRecordId = left.BirthRecordId, MatchedBirthRecordId = right.BirthRecordId,
            Score = 80, Reasons = "test",
        };
        db.DuplicateCandidates.Add(link);
        return link.DuplicateCandidateId;
    }

    private static DefaultHttpContext As(string subject, string role)
        => AuthTestContext.HttpContextFor(subject, client: null, role);

    private static DefaultHttpContext AsOfficer() => As(OfficerSubject, NcbrsRoles.DistrictOfficer);
    private static DefaultHttpContext AsMinistry() => As(MinistrySubject, NcbrsRoles.MinistryAdmin);

    private static T Controller<T>(T controller, DefaultHttpContext http) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static ReviewQueueScope QueueScope(NcbrsDbContext db, CurrentRegistrarService current)
        => new(new CountyScopeResolver(new CountyLookup(db)), current);

    private static DuplicateDetectionService Duplicates(NcbrsDbContext db)
        => new(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
            NullLogger<DuplicateDetectionService>.Instance, new CountyLookup(db));

    private DuplicatesController DuplicatesController(NcbrsDbContext db, DefaultHttpContext http)
    {
        var current = AuthTestContext.RegistrarService(db, http);
        return Controller(new DuplicatesController(Duplicates(db), current, QueueScope(db, current)), http);
    }

    private static AmendmentsController AmendmentsController(NcbrsDbContext db, DefaultHttpContext http)
    {
        var current = AuthTestContext.RegistrarService(db, http);
        var service = new AmendmentService(
            db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current, new CountyLookup(db));
        return Controller(new AmendmentsController(service, current, QueueScope(db, current)), http);
    }

    private static LateRegistrationsController LateRegistrationsController(NcbrsDbContext db, DefaultHttpContext http)
    {
        var current = AuthTestContext.RegistrarService(db, http);
        var service = new LateRegistrationService(db, current, new CountyLookup(db));
        return Controller(new LateRegistrationsController(service, current, QueueScope(db, current)), http);
    }

    private static int StatusOf<T>(ActionResult<T> result)
        => result.Result is ObjectResult objectResult ? objectResult.StatusCode ?? 200 : 200;

    private static IReadOnlyList<TItem> ItemsOf<TItem>(ActionResult<Page<TItem>> result)
        => ((result.Result as OkObjectResult)?.Value as Page<TItem> ?? result.Value!).Items;

    // --- duplicate review: the write that had no county check -------------------------------

    private async Task<int> ReviewAsync(DefaultHttpContext http, Guid pair)
    {
        await using var db = NewDb();
        var result = await DuplicatesController(db, http).Review(
            pair, new ApiRequest<ReviewDuplicateRequest> { Data = new ReviewDuplicateRequest { IsDuplicate = true } });

        return result is ObjectResult objectResult ? objectResult.StatusCode ?? 200 : 204;
    }

    private async Task<bool> SupersededAsync(Guid pair)
    {
        await using var db = NewDb();
        var link = await db.DuplicateCandidates.Include(l => l.BirthRecord).Include(l => l.MatchedBirthRecord)
            .SingleAsync(l => l.DuplicateCandidateId == pair);
        return link.BirthRecord!.SupersededByBirthRecordId is not null
               || link.MatchedBirthRecord!.SupersededByBirthRecordId is not null;
    }

    [Fact]
    public async Task AnOfficerDecidesTheirOwnCountysDuplicates()
    {
        Assert.Equal(StatusCodes.Status204NoContent, await ReviewAsync(AsOfficer(), _jubaPair));
        Assert.True(await SupersededAsync(_jubaPair));
    }

    /// <summary>
    /// The finding: confirming supersedes a registration and withdraws its
    /// certificate, and nothing stopped an officer doing that in another county.
    /// </summary>
    [Fact]
    public async Task AnOfficerCannotDecideAnotherCountysDuplicates()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await ReviewAsync(AsOfficer(), _terekekaPair));
        Assert.False(await SupersededAsync(_terekekaPair));
    }

    /// <summary>A pair spanning two counties acts on both, so it is the Ministry's call.</summary>
    [Fact]
    public async Task APairAcrossCountiesIsTheMinistrysCall()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await ReviewAsync(AsOfficer(), _crossPair));
        Assert.False(await SupersededAsync(_crossPair));

        Assert.Equal(StatusCodes.Status204NoContent, await ReviewAsync(AsMinistry(), _crossPair));
        Assert.True(await SupersededAsync(_crossPair));
    }

    // --- the queues: reading is where the leak was --------------------------------------------

    [Fact]
    public async Task TheDuplicateQueueShowsAnOfficerOnlyPairsWhollyInTheirCounty()
    {
        await using var db = NewDb();

        var officer = ItemsOf(await DuplicatesController(db, AsOfficer()).Pending());
        var ministry = ItemsOf(await DuplicatesController(db, AsMinistry()).Pending());

        Assert.Equal(["100001|100002"], officer.Select(p => string.Join('|', new[] { p.Brn, p.MatchedBrn }.Order())));
        Assert.Equal(3, ministry.Count);
    }

    [Fact]
    public async Task TheCorrectionQueueShowsAnOfficerOnlyTheirCounty()
    {
        await using var db = NewDb();

        Assert.Equal(["100001"], ItemsOf(await AmendmentsController(db, AsOfficer()).Pending()).Select(a => a.Brn));
        Assert.Equal(2, ItemsOf(await AmendmentsController(db, AsMinistry()).Pending()).Count);
    }

    [Fact]
    public async Task TheConflictQueueShowsAnOfficerOnlyTheirCounty()
    {
        await using var db = NewDb();

        Assert.Equal(["100001"], ItemsOf(await AmendmentsController(db, AsOfficer()).Conflicts()).Select(c => c.Brn));
        Assert.Equal(2, ItemsOf(await AmendmentsController(db, AsMinistry()).Conflicts()).Count);
    }

    [Fact]
    public async Task TheLateRegistrationQueueShowsAnOfficerOnlyTheirCounty()
    {
        await using var db = NewDb();

        Assert.Equal(["100001"], ItemsOf(await LateRegistrationsController(db, AsOfficer()).Pending()).Select(l => l.Brn));
        Assert.Equal(2, ItemsOf(await LateRegistrationsController(db, AsMinistry()).Pending()).Count);
    }

    /// <summary>
    /// Naming a facility in another county is refused, not answered with an
    /// empty list: empty would read as "nothing waiting there".
    /// </summary>
    [Fact]
    public async Task NamingAnotherCountysFacilityIsRefusedNotNarrowed()
    {
        await using var db = NewDb();

        Assert.Equal(StatusCodes.Status403Forbidden,
            StatusOf(await AmendmentsController(db, AsOfficer()).Pending(facilityId: Terekeka)));
        Assert.Equal(StatusCodes.Status403Forbidden,
            StatusOf(await AmendmentsController(db, AsOfficer()).Conflicts(facilityId: Terekeka)));
        Assert.Equal(StatusCodes.Status403Forbidden,
            StatusOf(await LateRegistrationsController(db, AsOfficer()).Pending(facilityId: Terekeka)));
        Assert.Equal(StatusCodes.Status403Forbidden,
            StatusOf(await DuplicatesController(db, AsOfficer()).Pending(facilityId: Terekeka)));

        // Their own facility is fine.
        Assert.Equal(StatusCodes.Status200OK,
            StatusOf(await AmendmentsController(db, AsOfficer()).Pending(facilityId: Juba)));
    }
}
