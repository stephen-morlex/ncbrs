using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Adding areas below a county (plan §17 item 31). The official dataset stops at
/// the payam, so without this a village post could only be placed at its payam.
/// What these pin: the registry gives the code, which can never collide with an
/// official p-code; the official levels are never added by hand; and the tree's
/// shape rules (strictly lower tier, rural with rural, urban with urban) hold.
/// </summary>
public class AdministrativeAreaCreationTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    private static readonly Registrar Ministry = new()
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

    private async Task<NcbrsDbContext> SeededAsync()
    {
        var db = new NcbrsDbContext(_database.Options);
        await AdministrativeAreaSeeder.SeedAsync(db);
        return db;
    }

    private static Task<AreaCreationOutcome> CreateAsync(NcbrsDbContext db, Guid parentId, AdministrativeLevel level, string name)
        => new AdministrativeAreaService(db, new CountyLookup(db)).CreateAsync(
            new CreateAdministrativeAreaRequest { ParentId = parentId, Level = level, Name = name }, Ministry, null);

    private static Task<AdministrativeArea> AreaAsync(NcbrsDbContext db, string code)
        => db.AdministrativeAreas.SingleAsync(area => area.Code == code);

    private static Task<AdministrativeArea> PayamInAsync(NcbrsDbContext db, string countyCode)
        => db.AdministrativeAreas.OrderBy(area => area.Code)
            .FirstAsync(area => area.Level == AdministrativeLevel.Payam && area.Parent!.Code == countyCode);

    [Fact]
    public async Task ABomaIsAddedUnderItsPayam_WithACodeTheRegistryGives()
    {
        await using var db = await SeededAsync();
        var payam = await PayamInAsync(db, "SS0101");

        var outcome = await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Boma, "  Gumbo   Sherikat ");

        Assert.Equal(AreaCreationResult.Created, outcome.Result);
        var boma = outcome.Area!;
        Assert.Equal("Gumbo Sherikat", boma.Name);
        Assert.Equal(payam.AdministrativeAreaId, boma.ParentId);
        Assert.Equal($"{payam.Code}-B01", boma.Code);

        var audit = await db.AuditLogs.SingleAsync(row => row.EntityId == boma.AdministrativeAreaId.ToString());
        Assert.Equal($"AdministrativeAreaCreated:{boma.Code}", audit.Action);
        Assert.Equal("SS0101", audit.CountyCode);
        Assert.Equal(Ministry.RegistrarId, audit.UserId);
    }

    /// <summary>A facility can now be placed at the village, and is still scoped by its county.</summary>
    [Fact]
    public async Task AVillageUnderABomaResolvesToItsCounty_SoAFacilityCanBePlacedThere()
    {
        await using var db = await SeededAsync();
        var payam = await PayamInAsync(db, "SS0101");
        var boma = (await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Boma, "Gumbo")).Area!;

        var village = (await CreateAsync(db, boma.AdministrativeAreaId, AdministrativeLevel.Village, "Lologo")).Area!;

        Assert.Equal($"{payam.Code}-B01-V01", village.Code);
        var facility = await new FacilityOnboardingService(db, new CountyLookup(db), new FacilityOnboardingOptions())
            .CreateAsync(new CreateFacilityRequest
            {
                Name = "Lologo Village Health Post",
                Tier = FacilityTier.VillageHealthPost,
                ConnectivityProfile = ConnectivityProfile.OfflineFirst,
                AdministrativeAreaId = village.AdministrativeAreaId,
            }, Ministry, null);

        Assert.Equal(FacilityOnboardingResult.Created, facility.Result);
        Assert.Equal("SS0101", facility.Facility!.CountyCode);
    }

    [Fact]
    public async Task SiblingsAreNumberedInTurn_AndANumberIsNeverReused()
    {
        await using var db = await SeededAsync();
        var payam = await PayamInAsync(db, "SS0101");

        var first = (await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Boma, "One")).Area!;
        var second = (await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Village, "Two")).Area!;
        var third = (await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Boma, "Three")).Area!;

        Assert.Equal($"{payam.Code}-B01", first.Code);
        Assert.Equal($"{payam.Code}-V01", second.Code);
        Assert.Equal($"{payam.Code}-B02", third.Code);

        // A gap (a code given and the row later renamed elsewhere, say) is not
        // filled: the next is after the highest code ever given.
        first.Code = $"{payam.Code}-B07";
        await db.SaveChangesAsync();
        var fourth = (await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Boma, "Four")).Area!;
        Assert.Equal($"{payam.Code}-B08", fourth.Code);
    }

    /// <summary>
    /// Official p-codes are letters and digits; every code the registry gives
    /// has a hyphen, so the two can never meet.
    /// </summary>
    [Fact]
    public async Task AGivenCodeCanNeverBeAnOfficialPCode()
    {
        await using var db = await SeededAsync();
        var payam = await PayamInAsync(db, "SS0101");

        var boma = (await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Boma, "Gumbo")).Area!;

        Assert.Contains('-', boma.Code);
        Assert.DoesNotContain(await db.AdministrativeAreas.Where(area => area.Code != boma.Code).Select(area => area.Code).ToListAsync(),
            code => code.Contains('-'));
    }

    [Fact]
    public async Task ANameAlreadyUsedUnderTheSameParentIsRefused_ButNotUnderAnother()
    {
        await using var db = await SeededAsync();
        var payams = await db.AdministrativeAreas.Where(area => area.Level == AdministrativeLevel.Payam && area.Parent!.Code == "SS0101")
            .OrderBy(area => area.Code).Take(2).ToListAsync();
        await CreateAsync(db, payams[0].AdministrativeAreaId, AdministrativeLevel.Village, "Gumbo");

        var again = await CreateAsync(db, payams[0].AdministrativeAreaId, AdministrativeLevel.Village, "gumbo");
        var elsewhere = await CreateAsync(db, payams[1].AdministrativeAreaId, AdministrativeLevel.Village, "Gumbo");

        Assert.Equal(AreaCreationResult.NameTaken, again.Result);
        Assert.Equal(AreaCreationResult.Created, elsewhere.Result);
    }

    /// <summary>States and counties are the official geography; a county is the scope officers act in.</summary>
    [Theory]
    [InlineData(AdministrativeLevel.State)]
    [InlineData(AdministrativeLevel.County)]
    [InlineData(AdministrativeLevel.Country)]
    public async Task TheOfficialLevelsAreNeverAdded(AdministrativeLevel level)
    {
        await using var db = await SeededAsync();
        var state = await AreaAsync(db, "SS01");

        var outcome = await CreateAsync(db, state.AdministrativeAreaId, level, "Somewhere");

        Assert.NotEqual(AreaCreationResult.Created, outcome.Result);
        Assert.False(new CreateAdministrativeAreaRequestValidator()
            .Validate(new CreateAdministrativeAreaRequest { ParentId = state.AdministrativeAreaId, Level = level, Name = "Somewhere" }).IsValid);
    }

    [Fact]
    public async Task NothingIsAddedAboveACounty()
    {
        await using var db = await SeededAsync();
        var state = await AreaAsync(db, "SS01");

        var outcome = await CreateAsync(db, state.AdministrativeAreaId, AdministrativeLevel.Payam, "Somewhere");

        Assert.Equal(AreaCreationResult.NotInACounty, outcome.Result);
    }

    [Theory]
    [InlineData(AdministrativeLevel.Payam, AdministrativeLevel.Quarter)]   // urban inside rural
    [InlineData(AdministrativeLevel.Block, AdministrativeLevel.Boma)]      // rural inside urban
    [InlineData(AdministrativeLevel.Boma, AdministrativeLevel.Payam)]      // a higher tier inside a lower
    [InlineData(AdministrativeLevel.Boma, AdministrativeLevel.Quarter)]    // the same tier
    public async Task TheTreesShapeHolds(AdministrativeLevel parentLevel, AdministrativeLevel childLevel)
    {
        await using var db = await SeededAsync();
        var payam = await PayamInAsync(db, "SS0101");
        var county = await AreaAsync(db, "SS0101");
        var parent = parentLevel switch
        {
            AdministrativeLevel.Payam => payam,
            AdministrativeLevel.Block => (await CreateAsync(db, county.AdministrativeAreaId, AdministrativeLevel.Block, "Block 1")).Area!,
            _ => (await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Boma, "Gumbo")).Area!,
        };

        var outcome = await CreateAsync(db, parent.AdministrativeAreaId, childLevel, "Somewhere");

        Assert.Equal(AreaCreationResult.WrongLevel, outcome.Result);
    }

    [Fact]
    public async Task AVillageMaySitDirectlyUnderACounty()
    {
        await using var db = await SeededAsync();
        var county = await AreaAsync(db, "SS0101");

        var outcome = await CreateAsync(db, county.AdministrativeAreaId, AdministrativeLevel.Village, "Lologo");

        Assert.Equal(AreaCreationResult.Created, outcome.Result);
        Assert.Equal("SS0101-V01", outcome.Area!.Code);
    }

    [Fact]
    public async Task AnUnknownParentIsNotFound()
    {
        await using var db = await SeededAsync();

        var outcome = await CreateAsync(db, Guid.CreateVersion7(), AdministrativeLevel.Village, "Lologo");

        Assert.Equal(AreaCreationResult.ParentNotFound, outcome.Result);
    }

    /// <summary>
    /// The seed upserts by code. A recorded area's code is one the seed never
    /// holds, so reseeding (every Development start) leaves it alone.
    /// </summary>
    [Fact]
    public async Task ReseedingLeavesAddedAreasAlone()
    {
        await using var db = await SeededAsync();
        var payam = await PayamInAsync(db, "SS0101");
        var boma = (await CreateAsync(db, payam.AdministrativeAreaId, AdministrativeLevel.Boma, "Gumbo")).Area!;

        await AdministrativeAreaSeeder.SeedAsync(db);

        var kept = await db.AdministrativeAreas.AsNoTracking().SingleAsync(area => area.AdministrativeAreaId == boma.AdministrativeAreaId);
        Assert.Equal(("Gumbo", payam.AdministrativeAreaId), (kept.Name, kept.ParentId!.Value));
    }

    /// <summary>The Ministry's act, like adding a facility.</summary>
    [Fact]
    public void AddingAnAreaIsTheMinistrys()
    {
        var create = typeof(AdministrativeAreasController).GetMethod(nameof(AdministrativeAreasController.Create))!;

        Assert.Equal(NcbrsRoles.CanManageFacilities, create.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }
}
