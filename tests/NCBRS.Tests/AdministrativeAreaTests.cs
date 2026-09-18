using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// South Sudan's administrative hierarchy: one self-referencing tree whose
/// levels vary by branch. These pin the two things a fixed column-per-level
/// design would lose — that a branch may stop early or skip a level, and that
/// the rural and urban names sit at the same tier rather than under each
/// other.
/// </summary>
public class AdministrativeAreaTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static AdministrativeArea Area(
        string name, AdministrativeLevel level, string code, AdministrativeArea? parent = null)
        => new() { Name = name, Level = level, Code = code, Parent = parent, ParentId = parent?.AdministrativeAreaId };

    // --- the level rules, which the device app validates offline too --------

    [Fact]
    public void AHigherTierContainsALowerOne_ButNeverTheReverseOrItsOwnTier()
    {
        Assert.True(AdministrativeLevels.CanContain(AdministrativeLevel.State, AdministrativeLevel.County));
        Assert.True(AdministrativeLevels.CanContain(AdministrativeLevel.County, AdministrativeLevel.Payam));
        Assert.True(AdministrativeLevels.CanContain(AdministrativeLevel.County, AdministrativeLevel.Block));
        Assert.True(AdministrativeLevels.CanContain(AdministrativeLevel.Payam, AdministrativeLevel.Boma));
        Assert.True(AdministrativeLevels.CanContain(AdministrativeLevel.Block, AdministrativeLevel.Quarter));

        // Flexible by design: a village may hang directly off a county where
        // no payam or boma was ever recorded.
        Assert.True(AdministrativeLevels.CanContain(AdministrativeLevel.County, AdministrativeLevel.Village));

        // Same tier does not nest, and nothing runs uphill.
        Assert.False(AdministrativeLevels.CanContain(AdministrativeLevel.Payam, AdministrativeLevel.Block));
        Assert.False(AdministrativeLevels.CanContain(AdministrativeLevel.Boma, AdministrativeLevel.Quarter));
        Assert.False(AdministrativeLevels.CanContain(AdministrativeLevel.County, AdministrativeLevel.State));
        Assert.False(AdministrativeLevels.CanContain(AdministrativeLevel.Country, AdministrativeLevel.Country));
    }

    [Fact]
    public void RuralAndUrbanLevelsAreNamedApartAtTheSameTier()
    {
        Assert.True(AdministrativeLevels.IsRural(AdministrativeLevel.Payam));
        Assert.True(AdministrativeLevels.IsRural(AdministrativeLevel.Boma));
        Assert.True(AdministrativeLevels.IsUrban(AdministrativeLevel.Block));
        Assert.True(AdministrativeLevels.IsUrban(AdministrativeLevel.Quarter));

        Assert.False(AdministrativeLevels.IsUrban(AdministrativeLevel.Payam));
        Assert.False(AdministrativeLevels.IsRural(AdministrativeLevel.Block));

        // A payam and a block are the same tier — both sit under a county.
        Assert.Equal(
            AdministrativeLevels.Tier(AdministrativeLevel.Payam),
            AdministrativeLevels.Tier(AdministrativeLevel.Block));
    }

    // --- resolving a facility's county / state up the chain -----------------

    [Fact]
    public void AncestorOfLevel_ResolvesTheCountyAndStateFromABoma()
    {
        var country = Area("South Sudan", AdministrativeLevel.Country, "SS");
        var state = Area("Central Equatoria", AdministrativeLevel.State, "SS-CE", country);
        var county = Area("Juba", AdministrativeLevel.County, "SS-CE-JUB", state);
        var payam = Area("Northern Bari", AdministrativeLevel.Payam, "SS-CE-JUB-NB", county);
        var boma = Area("Gudele", AdministrativeLevel.Boma, "SS-CE-JUB-NB-GUD", payam);

        Assert.Same(county, AdministrativeLevels.AncestorOfLevel(boma, AdministrativeLevel.County));
        Assert.Same(state, AdministrativeLevels.AncestorOfLevel(boma, AdministrativeLevel.State));
        Assert.Same(boma, AdministrativeLevels.AncestorOfLevel(boma, AdministrativeLevel.Boma));

        // The rural branch has no urban tier to find.
        Assert.Null(AdministrativeLevels.AncestorOfLevel(boma, AdministrativeLevel.Quarter));
    }

    // --- persistence --------------------------------------------------------

    [Fact]
    public async Task ARuralBranchRoundTripsWithItsWholeParentChain()
    {
        var country = Area("South Sudan", AdministrativeLevel.Country, "SS");
        var state = Area("Central Equatoria", AdministrativeLevel.State, "SS-CE", country);
        var county = Area("Juba", AdministrativeLevel.County, "SS-CE-JUB", state);
        var payam = Area("Northern Bari", AdministrativeLevel.Payam, "SS-CE-JUB-NB", county);
        var boma = Area("Gudele", AdministrativeLevel.Boma, "SS-CE-JUB-NB-GUD", payam);

        await using (var db = NewDb())
        {
            db.Database.EnsureCreated();
            db.AddRange(country, state, county, payam, boma);
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var loaded = await db.AdministrativeAreas
                .Include(a => a.Parent!).ThenInclude(a => a.Parent!).ThenInclude(a => a.Parent!).ThenInclude(a => a.Parent!)
                .FirstAsync(a => a.Code == "SS-CE-JUB-NB-GUD");

            Assert.Equal(AdministrativeLevel.Boma, loaded.Level);
            Assert.Equal("SS-CE-JUB", AdministrativeLevels.AncestorOfLevel(loaded, AdministrativeLevel.County)!.Code);
            Assert.Equal("SS-CE", AdministrativeLevels.AncestorOfLevel(loaded, AdministrativeLevel.State)!.Code);
            Assert.Null(AdministrativeLevels.AncestorOfLevel(loaded, AdministrativeLevel.Country)!.ParentId);
        }
    }

    [Fact]
    public async Task AnUrbanBranchPersistsUnderTheSameCounty()
    {
        var country = Area("South Sudan", AdministrativeLevel.Country, "SS");
        var state = Area("Central Equatoria", AdministrativeLevel.State, "SS-CE", country);
        var county = Area("Juba", AdministrativeLevel.County, "SS-CE-JUB", state);
        var block = Area("Juba Town", AdministrativeLevel.Block, "SS-CE-JUB-JT", county);
        var quarter = Area("Hai Malakal", AdministrativeLevel.Quarter, "SS-CE-JUB-JT-HM", block);

        await using (var db = NewDb())
        {
            db.Database.EnsureCreated();
            db.AddRange(country, state, county, block, quarter);
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var loaded = await db.AdministrativeAreas
                .Include(a => a.Parent!).ThenInclude(a => a.Parent!)
                .FirstAsync(a => a.Code == "SS-CE-JUB-JT-HM");

            Assert.Equal(AdministrativeLevel.Quarter, loaded.Level);
            Assert.Equal(AdministrativeLevel.Block, loaded.Parent!.Level);
            Assert.Equal("SS-CE-JUB", loaded.Parent!.Parent!.Code);
        }
    }

    [Fact]
    public async Task TwoAreasCannotShareACode()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();

        db.Add(Area("Juba", AdministrativeLevel.County, "SS-CE-JUB"));
        db.Add(Area("Juba (duplicate)", AdministrativeLevel.County, "SS-CE-JUB"));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
