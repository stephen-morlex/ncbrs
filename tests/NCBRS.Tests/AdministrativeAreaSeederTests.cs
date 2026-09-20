using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Seeding the national geography from the committed data file — the official
/// COD-AB for South Sudan (1 country, 11 admin-1, 79 counties, 512 payams).
/// These tests pin the COD totals and the invariants (idempotency, walking a
/// county up to its state, the vintage's treatment of Abyei and Pibor); a newer
/// COD vintage would update the totals here deliberately.
/// </summary>
public class AdministrativeAreaSeederTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    [Fact]
    public async Task SeedsTheWholeCodTree_CountryStatesCountiesAndPayams()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();

        await AdministrativeAreaSeeder.SeedAsync(db);

        Assert.Equal(1, await db.AdministrativeAreas.CountAsync(a => a.Level == AdministrativeLevel.Country));
        // COD-AB totals: 10 states + Abyei Region (admin-1), 79 counties, 512 payams.
        Assert.Equal(11, await db.AdministrativeAreas.CountAsync(a => a.Level == AdministrativeLevel.State));
        Assert.Equal(79, await db.AdministrativeAreas.CountAsync(a => a.Level == AdministrativeLevel.County));
        Assert.Equal(512, await db.AdministrativeAreas.CountAsync(a => a.Level == AdministrativeLevel.Payam));
    }

    [Fact]
    public async Task IsIdempotent_SafeToRunOnEveryStartup()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();

        await AdministrativeAreaSeeder.SeedAsync(db);
        var afterFirst = await db.AdministrativeAreas.CountAsync();

        await AdministrativeAreaSeeder.SeedAsync(db);
        var afterSecond = await db.AdministrativeAreas.CountAsync();

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task ACountyResolvesUpToItsState()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();
        await AdministrativeAreaSeeder.SeedAsync(db);

        var juba = await db.AdministrativeAreas.Include(a => a.Parent!).FirstAsync(a => a.Code == "SS0101");

        Assert.Equal(AdministrativeLevel.County, juba.Level);
        Assert.Equal("Central Equatoria", juba.Parent!.Name);
        Assert.Equal("SS01", AdministrativeLevels.AncestorOfLevel(juba, AdministrativeLevel.State)!.Code);
    }

    [Fact]
    public async Task AbyeiIsAdmin1AndPiborIsACounty_AsThisCodVintageEncodesThem()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();
        await AdministrativeAreaSeeder.SeedAsync(db);

        // Abyei Region is an admin-1 unit here, seeded at the state tier.
        var abyei = await db.AdministrativeAreas.FirstAsync(a => a.Code == "SS00");
        Assert.Equal(AdministrativeLevel.State, abyei.Level);

        // Pibor is a county under Jonglei in this vintage, not a separate
        // administrative area (see the data file's provenance note).
        var pibor = await db.AdministrativeAreas.Include(a => a.Parent!).FirstAsync(a => a.Code == "SS0308");
        Assert.Equal(AdministrativeLevel.County, pibor.Level);
        Assert.Equal("Jonglei", pibor.Parent!.Name);
    }
}
