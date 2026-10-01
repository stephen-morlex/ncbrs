using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Web;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Bringing a facility into the registry (pilot readiness §1). Before this,
/// only the Development seed could create one. What these pin is that the two
/// things a facility needs that must never be typed by hand -- its county and
/// its range of registration numbers -- are computed, and that no two
/// facilities can ever hold overlapping ranges.
/// </summary>
public class FacilityOnboardingTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();
    private readonly Registrar _ministry = new()
    {
        RegistrarId = Guid.CreateVersion7(),
        DisplayName = "Ministry Admin",
        Role = RegistrarRole.MinistryAdmin,
    };

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private async Task<NcbrsDbContext> SeededAsync()
    {
        var db = NewDb();
        await AdministrativeAreaSeeder.SeedAsync(db);
        return db;
    }

    private static Task<FacilityOnboardingOutcome> CreateAsync(NcbrsDbContext db, string name, Guid areaId, long rangeSize = 100_000)
        => new FacilityOnboardingService(db, new CountyLookup(db), new FacilityOnboardingOptions { BrnRangeSize = rangeSize })
            .CreateAsync(new CreateFacilityRequest
            {
                Name = name,
                Tier = FacilityTier.VillageHealthPost,
                ConnectivityProfile = ConnectivityProfile.OfflineFirst,
                AdministrativeAreaId = areaId,
            }, new Registrar { RegistrarId = Guid.CreateVersion7(), DisplayName = "Ministry Admin", Role = RegistrarRole.MinistryAdmin }, null);

    private static Task<AdministrativeArea> PayamInAsync(NcbrsDbContext db, string countyCode)
        => db.AdministrativeAreas.FirstAsync(area => area.Level == AdministrativeLevel.Payam && area.Parent!.Code == countyCode);

    [Fact]
    public async Task AFacilityIsPlacedInItsCountyFromTheTree_AndGivenARange()
    {
        await using var db = await SeededAsync();
        var payam = await PayamInAsync(db, "SS0101");

        var outcome = await CreateAsync(db, "Rejaf Village Health Post", payam.AdministrativeAreaId);

        Assert.Equal(FacilityOnboardingResult.Created, outcome.Result);
        var facility = outcome.Facility!;
        Assert.Equal("SS0101", facility.CountyCode);
        Assert.Equal(payam.AdministrativeAreaId, facility.AdministrativeAreaId);
        Assert.Equal(100_000, facility.BrnBlockStart);
        Assert.Equal(199_999, facility.BrnBlockEnd);
        Assert.Equal(100_000, facility.BrnBlockNextAvailable);

        var audit = await db.AuditLogs.SingleAsync(row => row.EntityId == facility.FacilityId.ToString());
        Assert.Equal("FacilityCreated:100000-199999", audit.Action);
        Assert.Equal("SS0101", audit.CountyCode);
    }

    /// <summary>
    /// Decision #2 rests on ranges never overlapping: two facilities sharing
    /// numbers would give two children one BRN, found years later.
    /// </summary>
    [Fact]
    public async Task EachFacilityGetsTheNextRange_AboveEveryRangeAlreadyGiven()
    {
        await using var db = await SeededAsync();
        var payam = await PayamInAsync(db, "SS0101");

        // A facility seeded with an odd range: the next starts on the next
        // aligned boundary above it, never inside it.
        db.Facilities.Add(new Facility
        {
            Name = "Seeded", CountyCode = "SS0101", BrnBlockStart = 300_000, BrnBlockEnd = 312_345, BrnBlockNextAvailable = 300_000,
        });
        await db.SaveChangesAsync();

        var first = (await CreateAsync(db, "First", payam.AdministrativeAreaId)).Facility!;
        var second = (await CreateAsync(db, "Second", payam.AdministrativeAreaId)).Facility!;

        Assert.Equal(400_000, first.BrnBlockStart);
        Assert.Equal(500_000, second.BrnBlockStart);
        Assert.True(first.BrnBlockEnd < second.BrnBlockStart);
    }

    /// <summary>
    /// The race two simultaneous onboardings would run is closed by the
    /// database, not by hoping they never coincide: a second real range on
    /// the same start is refused. Facilities with no range yet (0-0) are not.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesTwoFacilitiesStartingTheSameRange()
    {
        await using (var db = NewDb())
        {
            db.Facilities.AddRange(
                new Facility { Name = "No range A", CountyCode = "SS0101" },
                new Facility { Name = "No range B", CountyCode = "SS0101" },
                new Facility { Name = "A", CountyCode = "SS0101", BrnBlockStart = 100_000, BrnBlockEnd = 199_999 });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            db.Facilities.Add(new Facility { Name = "B", CountyCode = "SS0102", BrnBlockStart = 100_000, BrnBlockEnd = 199_999 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task AFacilityIsNotPlacedAboveACounty()
    {
        await using var db = await SeededAsync();
        var state = await db.AdministrativeAreas.FirstAsync(area => area.Code == "SS01");

        var outcome = await CreateAsync(db, "Nowhere in particular", state.AdministrativeAreaId);

        Assert.Equal(FacilityOnboardingResult.NotInACounty, outcome.Result);
        Assert.False(await db.Facilities.AnyAsync());
    }

    [Fact]
    public async Task AnUnknownAreaIsRefused()
    {
        await using var db = await SeededAsync();

        Assert.Equal(FacilityOnboardingResult.AreaNotFound, (await CreateAsync(db, "Lost", Guid.CreateVersion7())).Result);
    }

    /// <summary>Two facilities with one name in one county would be told apart by nobody.</summary>
    [Fact]
    public async Task ANameIsUniqueWithinItsCounty_NotNationally()
    {
        await using var db = await SeededAsync();
        var juba = await PayamInAsync(db, "SS0101");
        var otherCounty = await db.AdministrativeAreas.FirstAsync(area => area.Level == AdministrativeLevel.County && area.Code != "SS0101");

        Assert.Equal(FacilityOnboardingResult.Created, (await CreateAsync(db, "Gumbo PHCU", juba.AdministrativeAreaId)).Result);
        Assert.Equal(FacilityOnboardingResult.NameTaken, (await CreateAsync(db, "  gumbo phcu ", juba.AdministrativeAreaId)).Result);
        Assert.Equal(FacilityOnboardingResult.Created, (await CreateAsync(db, "Gumbo PHCU", otherCounty.AdministrativeAreaId)).Result);
    }

    /// <summary>A facility comes with a national range of numbers: the Ministry's act, not a district's.</summary>
    [Fact]
    public void CreatingAFacilityIsTheMinistrysAct()
    {
        var create = typeof(FacilitiesController).GetMethod(nameof(FacilitiesController.Create))!;

        Assert.Equal(NcbrsRoles.CanManageFacilities, create.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }
}
