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
            DisplayName = "Nurse A. Banda",
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
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance, new DistrictLookup(db)),
                Options.Create(new StatutoryRegistrationOptions())),
            new AmendmentService(db, publisher, new CertificateRevocationRecorder(db), currentRegistrar, new DistrictLookup(db)),
            currentRegistrar,
            new DistrictLookup(db),
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
}
