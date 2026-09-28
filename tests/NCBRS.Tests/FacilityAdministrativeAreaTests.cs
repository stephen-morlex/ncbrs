using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Facilities placed in the administrative hierarchy, and the Development
/// reseed that replaces the old placeholder sample rows with South Sudan ones.
/// </summary>
public class FacilityAdministrativeAreaTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    [Fact]
    public async Task AFacilityResolvesItsCountyAndStateThroughItsArea()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();
        await AdministrativeAreaSeeder.SeedAsync(db);

        var juba = await db.AdministrativeAreas.FirstAsync(a => a.Code == "SS0101");
        db.Facilities.Add(new Facility
        {
            Name = "A clinic in Juba",
            Tier = FacilityTier.Clinic,
            ConnectivityProfile = ConnectivityProfile.Intermittent,
            CountyCode = juba.Code,
            AdministrativeAreaId = juba.AdministrativeAreaId,
        });
        await db.SaveChangesAsync();

        var loaded = await db.Facilities
            .Include(f => f.AdministrativeArea!).ThenInclude(a => a.Parent!)
            .FirstAsync(f => f.Name == "A clinic in Juba");

        Assert.Equal(AdministrativeLevel.County, loaded.AdministrativeArea!.Level);
        Assert.Equal("SS01", AdministrativeLevels.AncestorOfLevel(loaded.AdministrativeArea!, AdministrativeLevel.State)!.Code);
    }

    [Fact]
    public async Task TheDevelopmentReseedPlacesTheFleetInPayamsWithTheCountyAsDistrict()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();
        await AdministrativeAreaSeeder.SeedAsync(db);

        await DevelopmentDataSeeder.SeedAsync(db);

        var facilities = await db.Facilities
            .Include(f => f.AdministrativeArea!).ThenInclude(a => a.Parent!)
            .ToListAsync();
        Assert.Equal(8, facilities.Count);

        // Every fleet facility sits at a payam, and its transitional CountyCode
        // carries the county p-code — the level scoping resolves up to.
        Assert.All(facilities, f =>
        {
            Assert.NotNull(f.AdministrativeAreaId);
            Assert.Equal(AdministrativeLevel.Payam, f.AdministrativeArea!.Level);
            var county = AdministrativeLevels.AncestorOfLevel(f.AdministrativeArea!, AdministrativeLevel.County);
            Assert.NotNull(county);
            Assert.Equal(county!.Code, f.CountyCode);
        });
    }

    [Fact]
    public async Task TheDevelopmentReseedKeepsTheDevRealmSubjectsSoLoginsStillResolve()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();
        await AdministrativeAreaSeeder.SeedAsync(db);
        await DevelopmentDataSeeder.SeedAsync(db);

        var subjects = await db.Registrars.Select(r => r.ExternalSubjectId).ToListAsync();

        // Exactly the dev realm's users -- its people and the District node's
        // service account -- so a login or a node token always resolves to a
        // registrar, and neither side can gain an identity the other lacks.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NCBRS.slnx")))
        {
            directory = directory.Parent;
        }

        using var realm = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory!.FullName, "keycloak", "ncbrs-realm.json")));
        var realmUsers = realm.RootElement.GetProperty("users").EnumerateArray()
            .Select(user => user.GetProperty("id").GetString()!).Order().ToList();

        Assert.Equal(realmUsers, subjects.Select(subject => subject ?? "").Order().ToList());
        Assert.Contains(await db.Registrars.ToListAsync(), r => r.Role == RegistrarRole.MinistryAdmin);
    }

    [Fact]
    public async Task TheDevelopmentReseedIsIdempotent()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();
        await AdministrativeAreaSeeder.SeedAsync(db);

        await DevelopmentDataSeeder.SeedAsync(db);
        var facilities = await db.Facilities.CountAsync();
        var registrars = await db.Registrars.CountAsync();

        await DevelopmentDataSeeder.SeedAsync(db);

        Assert.Equal(facilities, await db.Facilities.CountAsync());
        Assert.Equal(registrars, await db.Registrars.CountAsync());
    }
}
