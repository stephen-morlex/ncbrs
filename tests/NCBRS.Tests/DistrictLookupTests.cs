using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Resolving the county an act is accountable to. The point of the rework: the
/// stamp follows the administrative hierarchy, not a free string — a facility
/// deep in a boma is still accounted for by its county — while a facility not
/// yet linked to an area falls back to its legacy district so nothing goes
/// unstamped during the transition.
/// </summary>
public class DistrictLookupTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    [Fact]
    public async Task ResolvesTheCountyByWalkingUpFromASubCountyArea()
    {
        Guid facilityId;
        await using (var db = NewDb())
        {
            db.Database.EnsureCreated();
            await AdministrativeAreaSeeder.SeedAsync(db);

            var juba = await db.AdministrativeAreas.FirstAsync(a => a.Code == "SS-CE-JUB");
            var payam = new AdministrativeArea
            {
                Name = "Northern Bari", Level = AdministrativeLevel.Payam,
                Code = "SS-CE-JUB-NB", ParentId = juba.AdministrativeAreaId,
            };
            db.AdministrativeAreas.Add(payam);

            var facility = new Facility
            {
                Name = "Northern Bari Health Post",
                Tier = FacilityTier.VillageHealthPost,
                ConnectivityProfile = ConnectivityProfile.OfflineFirst,
                DistrictId = "legacy-should-be-ignored",
                AdministrativeAreaId = payam.AdministrativeAreaId,
            };
            db.Facilities.Add(facility);
            await db.SaveChangesAsync();
            facilityId = facility.FacilityId;
        }

        var lookup = new DistrictLookup(NewDb());
        Assert.Equal("SS-CE-JUB", await lookup.ForFacilityAsync(facilityId));
    }

    [Fact]
    public async Task FallsBackToTheLegacyDistrictWhenNoAreaIsLinked()
    {
        Guid facilityId;
        await using (var db = NewDb())
        {
            db.Database.EnsureCreated();
            var facility = new Facility
            {
                Name = "Unlinked clinic",
                Tier = FacilityTier.Clinic,
                ConnectivityProfile = ConnectivityProfile.Intermittent,
                DistrictId = "D-LEGACY-01",
            };
            db.Facilities.Add(facility);
            await db.SaveChangesAsync();
            facilityId = facility.FacilityId;
        }

        var lookup = new DistrictLookup(NewDb());
        Assert.Equal("D-LEGACY-01", await lookup.ForFacilityAsync(facilityId));
    }
}
