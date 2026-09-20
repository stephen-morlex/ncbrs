using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers BirthRecordsController.RequestBrnBlock: the concurrency guard,
/// the BrnBlockEnd ceiling, and blockSize validation. Uses a real SQLite
/// connection (not the EF InMemory provider) because the concurrency-token
/// behavior under test relies on provider-generated WHERE clauses that the
/// InMemory provider doesn't enforce the same way.
/// </summary>
public class BrnBlockAllocationTests
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    private static async Task<DbContextOptions<NcbrsDbContext>> SeedAsync(TestDatabase database, Facility facility)
    {
        var options = database.Options;
        await using var db = new NcbrsDbContext(options);
        db.Facilities.Add(facility);
        db.Registrars.Add(TestRegistrar());
        await db.SaveChangesAsync();
        return options;
    }

    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private static Facility TestFacility(string name, long start, long end, long nextAvailable)
        => new()
        {
            FacilityId = FacilityId,
            Name = name,
            DistrictId = "D1",
            BrnBlockStart = start,
            BrnBlockEnd = end,
            BrnBlockNextAvailable = nextAvailable
        };

    /// <summary>
    /// The caller is now resolved from the token's subject, so the tests need
    /// a registrar whose ExternalSubjectId matches the principal they present.
    /// </summary>
    private static Registrar TestRegistrar()
        => new()
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Lado",
            Role = RegistrarRole.FacilityRegistrar,
            CredentialHash = "test"
        };

    private static ApiRequest<BrnBlockRequest> Block(int blockSize, string? deviceId = null)
        => new() { Data = new BrnBlockRequest { BlockSize = blockSize, DeviceId = deviceId } };

    /// <summary>
    /// Rejections share one shape (ApiErrorResponse at a given status), so
    /// assert on that rather than on the specific IActionResult subclass.
    /// </summary>
    private static ApiErrorResponse AssertError(IActionResult? result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        var error = Assert.IsType<ApiErrorResponse>(objectResult.Value);
        Assert.NotEmpty(error.Errors);
        return error;
    }

    /// <summary>
    /// The controller reads the ambient transaction context to stamp audit
    /// rows, so it needs a real HttpContext even in a unit test.
    /// </summary>
    private static BirthRecordsController CreateController(NcbrsDbContext db, params string[] roles)
    {
        var http = AuthTestContext.HttpContextFor(roles: roles);
        var currentRegistrar = AuthTestContext.RegistrarService(db, http);

        var publisher = new NoOpEventPublisher();

        return new BirthRecordsController(
            db,
            new BirthRegistrationService(db, publisher, currentRegistrar,
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance, new CountyLookup(db)),
                new CountyLookup(db),
                Options.Create(new StatutoryRegistrationOptions())),
            new AmendmentService(db, publisher, new CertificateRevocationRecorder(db), currentRegistrar, new CountyLookup(db)),
            currentRegistrar,
            new CountyLookup(db),
            Options.Create(new StatutoryRegistrationOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    [Fact]
    public async Task SequentialRequests_ReturnNonOverlappingContiguousBlocks()
    {
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 1000, 5000, 1000));

        await using var db = new NcbrsDbContext(options);
        var controller = CreateController(db);

        var first = Assert.IsType<BrnBlockResponse>((await controller.RequestBrnBlock(FacilityId, Block(200))).Value);
        var second = Assert.IsType<BrnBlockResponse>((await controller.RequestBrnBlock(FacilityId, Block(200))).Value);

        Assert.Equal((1000L, 1199L), (first.BlockStart, first.BlockEnd));
        Assert.Equal((1200L, 1399L), (second.BlockStart, second.BlockEnd));
    }

    [Fact]
    public async Task Request_ClampsToBrnBlockEnd_WhenFullSizeDoesNotFit()
    {
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Near-Ceiling Clinic", 1000, 5000, 4950));

        await using var db = new NcbrsDbContext(options);
        var controller = CreateController(db);

        var response = Assert.IsType<BrnBlockResponse>((await controller.RequestBrnBlock(FacilityId, Block(200))).Value);

        Assert.Equal(4950, response.BlockStart);
        Assert.Equal(5000, response.BlockEnd); // clamped, not 5149
    }

    [Fact]
    public async Task Request_ReturnsConflict_WhenBlockAlreadyExhausted()
    {
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Exhausted Clinic", 1000, 5000, 5001));

        await using var db = new NcbrsDbContext(options);
        var controller = CreateController(db);

        // blockSize range now lives on the DTO as [Range]; see ValidationTests.
        var result = await controller.RequestBrnBlock(FacilityId, Block(200));

        AssertError(result.Result, StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// A facility-scoped registrar gets 403 for any facility but their own --
    /// including one that doesn't exist. Answering 404 here would let a
    /// caller enumerate which facility ids are real.
    /// </summary>
    [Fact]
    public async Task Request_ForAnotherFacility_IsForbidden_NotFound()
    {
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 1000, 5000, 1000));

        await using var db = new NcbrsDbContext(options);
        var controller = CreateController(db);

        var result = await controller.RequestBrnBlock(Guid.NewGuid(), Block(200));

        AssertError(result.Result, StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// A ministry admin may act across facilities, so for them the lookup
    /// proceeds and an unknown facility is genuinely a 404.
    /// </summary>
    [Fact]
    public async Task Request_ByMinistryAdmin_ReturnsNotFound_WhenFacilityDoesNotExist()
    {
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 1000, 5000, 1000));

        await using var db = new NcbrsDbContext(options);
        var controller = CreateController(db, NcbrsRoles.MinistryAdmin);

        var result = await controller.RequestBrnBlock(Guid.NewGuid(), Block(200));

        AssertError(result.Result, StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Forces a genuine DbUpdateConcurrencyException by pre-tracking a
    /// stale snapshot of the facility in one context (simulating a request
    /// that read the row before a concurrent writer committed), then
    /// committing a change to the same row through a second, independent
    /// context. The controller under test must retry against fresh data
    /// and must not hand out a range that overlaps what the "other writer"
    /// already claimed.
    /// </summary>
    [Fact]
    public async Task Request_RetriesAndAvoidsOverlap_WhenAnotherWriterAdvancesCounterFirst()
    {
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Contended Clinic", 1000, 5000, 1000));

        await using var contextA = new NcbrsDbContext(options);
        // Warm contextA's identity map with the pre-conflict snapshot
        // (BrnBlockNextAvailable = 1000) before the "other writer" commits.
        var preloaded = await contextA.Facilities.FindAsync(FacilityId);
        Assert.NotNull(preloaded);

        // Simulate a concurrent request that already claimed [1000, 1199]
        // and committed, via a separate DbContext instance.
        await using (var contextB = new NcbrsDbContext(options))
        {
            var facilityB = await contextB.Facilities.FindAsync(FacilityId);
            facilityB!.BrnBlockNextAvailable = 1200;
            await contextB.SaveChangesAsync();
        }

        var controller = CreateController(contextA);

        var response = Assert.IsType<BrnBlockResponse>((await controller.RequestBrnBlock(FacilityId, Block(200))).Value);

        Assert.Equal(1200, response.BlockStart);
        Assert.Equal(1399, response.BlockEnd);

        await using var verifyDb = new NcbrsDbContext(options);
        var finalFacility = await verifyDb.Facilities.FindAsync(FacilityId);
        Assert.Equal(1400, finalFacility!.BrnBlockNextAvailable);
    }

    // ---- Numbers already on a record --------------------------------------
    //
    // BrnBlockNextAvailable tracks what has been granted, not what has been
    // used, and the two diverge whenever a record enters carrying a BRN from
    // the facility's range without a grant -- a sync from a device provisioned
    // elsewhere, a restored dump, a seeded environment.
    //
    // Reported live: a facility whose counter sat at 200000 while 200000 and
    // 200001 were both registered. The grant handed out 200000 and the
    // registration was refused as a duplicate.

    [Fact]
    public async Task AGrantSkipsANumberAlreadyOnARecord()
    {
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 200_000, 299_999, 200_000));

        await GivenRegisteredAsync(options, "200000");

        await using var db = new NcbrsDbContext(options);
        var response = Assert.IsType<BrnBlockResponse>(
            (await CreateController(db).RequestBrnBlock(FacilityId, Block(1))).Value);

        // Not 200000, which is on a record and can never be registered again.
        Assert.Equal(200_001, response.BlockStart);
    }

    [Fact]
    public async Task AGrantSkipsAWholeRunOfUsedNumbers()
    {
        // The reported case exactly: two consecutive numbers used, counter at
        // the first. One request must clear both rather than handing out a
        // refusal twice.
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 200_000, 299_999, 200_000));

        await GivenRegisteredAsync(options, "200000", "200001");

        await using var db = new NcbrsDbContext(options);
        var response = Assert.IsType<BrnBlockResponse>(
            (await CreateController(db).RequestBrnBlock(FacilityId, Block(1))).Value);

        Assert.Equal(200_002, response.BlockStart);
    }

    [Fact]
    public async Task AGrantedBlockNeverContainsAUsedNumber()
    {
        // A device takes its block offline. Every number in it must be one it
        // can actually register against -- a collision is not discovered until
        // it syncs, a fortnight of births later.
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 200_000, 299_999, 200_000));

        await GivenRegisteredAsync(options, "200000", "200003");

        await using var db = new NcbrsDbContext(options);
        var response = Assert.IsType<BrnBlockResponse>(
            (await CreateController(db).RequestBrnBlock(FacilityId, Block(10))).Value);

        await using var verify = new NcbrsDbContext(options);
        var used = verify.BirthRecords.Select(record => record.Brn).ToList();

        for (var number = response.BlockStart; number <= response.BlockEnd; number++)
        {
            Assert.DoesNotContain(number.ToString(CultureInfo.InvariantCulture), used);
        }
    }

    [Fact]
    public async Task SkippingIsRecordedInTheAuditTrail()
    {
        // A counter behind the register means records reached this range
        // outside the grant path. Somebody should be able to find out that
        // happened, and how often.
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 200_000, 299_999, 200_000));

        await GivenRegisteredAsync(options, "200000", "200001");

        await using var db = new NcbrsDbContext(options);
        await CreateController(db).RequestBrnBlock(FacilityId, Block(1));

        await using var verify = new NcbrsDbContext(options);
        var actions = verify.AuditLogs.Select(log => log.Action).ToList();

        Assert.Contains(actions, action => action.StartsWith("BrnBlockGranted:skipped="));
    }

    [Fact]
    public async Task AnOrdinaryGrantIsNotRecordedAsSkipping()
    {
        // The negative that keeps the signal worth reading. If every grant
        // carried a skip count, nobody would notice the ones that did.
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 1000, 5000, 1000));

        await using var db = new NcbrsDbContext(options);
        await CreateController(db).RequestBrnBlock(FacilityId, Block(200));

        await using var verify = new NcbrsDbContext(options);
        var actions = verify.AuditLogs.Select(log => log.Action).ToList();

        Assert.Contains("BrnBlockGranted", actions);
        Assert.DoesNotContain(actions, action => action.StartsWith("BrnBlockGranted:skipped="));
    }

    [Fact]
    public async Task AProvisionalIdentifierNeverBlocksANumber()
    {
        // A PROV- identifier was never drawn from a block and cannot collide
        // with one. Treating it as used would burn numbers for nothing.
        using var database = TestDatabase.Create();
        var options = await SeedAsync(database, TestFacility("Test Clinic", 200_000, 299_999, 200_000));

        await GivenRegisteredAsync(options, "PROV-TABLET07-1");

        await using var db = new NcbrsDbContext(options);
        var response = Assert.IsType<BrnBlockResponse>(
            (await CreateController(db).RequestBrnBlock(FacilityId, Block(1))).Value);

        Assert.Equal(200_000, response.BlockStart);
    }

    private static async Task GivenRegisteredAsync(
        DbContextOptions<NcbrsDbContext> options, params string[] brns)
    {
        await using var db = new NcbrsDbContext(options);

        foreach (var brn in brns)
        {
            db.BirthRecords.Add(new BirthRecord
            {
                Brn = brn,
                VitalEventType = VitalEventType.LiveBirth,
                ChildPerson = new Person { FullName = "Ayen Deng" },
                FacilityId = FacilityId,
                RegisteredByRegistrarId = RegistrarId,
                DateOfBirth = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                Sex = Sex.Female,
                Plurality = BirthPlurality.Singleton,
                Status = RecordStatus.Provisional,
            });
        }

        await db.SaveChangesAsync();
    }
}
