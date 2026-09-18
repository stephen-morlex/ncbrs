using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Seeding the national geography from the committed data file. The counties
/// in that file are unverified and will change; these tests pin the shape and
/// the invariants (the reliable top of the tree, idempotency, county→state
/// resolution) rather than exact county counts, so correcting the file does
/// not break them.
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
    public async Task SeedsTheCountryTheTenStatesAndTheThreeAdministrativeAreas()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();

        await AdministrativeAreaSeeder.SeedAsync(db);

        Assert.Equal(1, await db.AdministrativeAreas.CountAsync(a => a.Level == AdministrativeLevel.Country));
        // 10 states + Abyei, Greater Pibor, Ruweng (state-equivalent).
        Assert.Equal(13, await db.AdministrativeAreas.CountAsync(a => a.Level == AdministrativeLevel.State));
        Assert.True(await db.AdministrativeAreas.CountAsync(a => a.Level == AdministrativeLevel.County) > 0);
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

        var juba = await db.AdministrativeAreas.Include(a => a.Parent!).FirstAsync(a => a.Code == "SS-CE-JUB");

        Assert.Equal(AdministrativeLevel.County, juba.Level);
        Assert.Equal("Central Equatoria", juba.Parent!.Name);
        Assert.Equal("SS-CE", AdministrativeLevels.AncestorOfLevel(juba, AdministrativeLevel.State)!.Code);
    }

    [Fact]
    public async Task TheAdministrativeAreasSitAtTheStateTierWithNoCounties()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();
        await AdministrativeAreaSeeder.SeedAsync(db);

        foreach (var code in new[] { "SS-AB", "SS-GP", "SS-RW" })
        {
            var area = await db.AdministrativeAreas.FirstAsync(a => a.Code == code);
            Assert.Equal(AdministrativeLevel.State, area.Level);
            Assert.Equal(0, await db.AdministrativeAreas.CountAsync(a => a.ParentId == area.AdministrativeAreaId));
        }
    }
}
