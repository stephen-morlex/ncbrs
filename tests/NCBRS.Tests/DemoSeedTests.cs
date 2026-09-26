using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The development seed, whole, through the real registration path and the
/// real duplicate scan -- the dataset every developer and every demo starts
/// from.
///
/// It pins both halves of plan §17 11e at once. Before the fix, the seed's
/// review queue held five candidates and all five were wrong: the seed draws
/// names from small pools, and the matcher flagged different people who shared
/// one word of their names. Now it must hold exactly one, and it must be the
/// duplicate the seed plants on purpose -- the same child registered at a
/// village post and again at a hospital.
/// </summary>
public class DemoSeedTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync()
    {
        await using var db = new NcbrsDbContext(_database.Options);

        await AdministrativeAreaSeeder.SeedAsync(db);
        await DevelopmentDataSeeder.SeedAsync(db);

        var counties = new CountyLookup(db);
        var current = AuthTestContext.RegistrarService(db, AuthTestContext.HttpContextFor());
        var registration = new BirthRegistrationService(
            db,
            new NoOpEventPublisher(),
            current,
            new DuplicateDetectionService(
                db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
                NullLogger<DuplicateDetectionService>.Instance, counties),
            counties,
            Options.Create(new StatutoryRegistrationOptions()));

        await DemoBirthSeeder.SeedAsync(db, registration);
    }

    [Fact]
    public async Task TheSeedFlagsExactlyTheDuplicateItPlants()
    {
        await SeedAsync();

        await using var db = new NcbrsDbContext(_database.Options);

        var candidate = Assert.Single(await db.DuplicateCandidates.ToListAsync());

        var pair = await db.BirthRecords
            .Include(record => record.Facility)
            .Include(record => record.ChildPerson)
            .Include(record => record.MotherPerson)
            .Where(record => record.BirthRecordId == candidate.BirthRecordId
                             || record.BirthRecordId == candidate.MatchedBirthRecordId)
            .ToListAsync();

        Assert.Equal(2, pair.Count);
        Assert.Contains(pair, record => record.Facility!.Tier == FacilityTier.VillageHealthPost);
        Assert.Contains(pair, record => record.Facility!.Tier == FacilityTier.Hospital);
        Assert.Equal(pair[0].MotherPerson!.FullName, pair[1].MotherPerson!.FullName);
        Assert.NotEqual(pair[0].ChildPerson!.FullName, pair[1].ChildPerson!.FullName);
        Assert.True(candidate.Score >= DuplicateMatcher.ReviewThreshold);
    }

    /// <summary>
    /// The planted re-registration is a legal registration like any other:
    /// it must go through the real path cleanly, confirmed against its block.
    /// </summary>
    [Fact]
    public async Task EverySeededBirthIsRegisteredAndConfirmed()
    {
        await SeedAsync();

        await using var db = new NcbrsDbContext(_database.Options);

        Assert.True(await db.BirthRecords.CountAsync() > 0);
        Assert.False(await db.BirthRecords.AnyAsync(record => record.ConfirmedAtUtc == null));
    }
}
