using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Nothing in the registry deletes a record on another row's behalf.
///
/// The register's own rule is that nothing is deleted: an annulment keeps the
/// record, a duplicate supersession keeps both, a revoked certificate keeps
/// its revocation, and the audit trail cannot be rewritten at all. Until this
/// was enforced, the schema quietly disagreed — nine relationships were left
/// at EF's default for a required relationship, which is Cascade.
///
/// These are the paths that destroyed records. Each asserts the delete is
/// **refused**, not that it deletes tidily.
/// </summary>
public class NoCascadeDeleteTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private const string Brn = "100001";

    private static readonly DateTime Born = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public NoCascadeDeleteTests()
    {
        // migrated: true, not EnsureCreated. The delete behaviour asserted
        // here is written by a migration, and EnsureCreated builds schema
        // from the model without running one -- a test of an absent
        // constraint would pass for the wrong reason.
        _database = TestDatabase.Create(migrated: true);
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId,
            Name = "Kabwe Village Health Post",
            DistrictId = "D-CENTRAL-07",
        });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Banda",
        });

        db.BirthRecords.Add(new BirthRecord
        {
            Brn = Brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Chipo Mwale" },
            FacilityId = FacilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = Born,
            Sex = Sex.Female,
            BirthWeightGrams = 3200,
            Plurality = BirthPlurality.Singleton,
            Status = RecordStatus.Confirmed,
        });

        db.SaveChanges();
    }

    [Fact]
    public void Deleting_a_facility_cannot_take_its_births_with_it()
    {
        // The worst of the six. A facility closes, someone tidies the row,
        // and every birth ever registered there is gone -- with no annulment,
        // no audit entry, and no BRN left resolving to an explanation.
        using var db = new NcbrsDbContext(_options);

        db.Facilities.Remove(db.Facilities.Single());

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());

        AssertTheBirthSurvived();
    }

    [Fact]
    public void Deleting_a_registrar_cannot_take_the_births_they_filed()
    {
        // Withdrawing someone's access is a Keycloak act. Removing them from
        // the registry is not, because what they did cannot be unmade.
        using var db = new NcbrsDbContext(_options);

        db.Registrars.Remove(db.Registrars.Single());

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());

        AssertTheBirthSurvived();
    }

    [Fact]
    public void Deleting_a_registrar_cannot_take_a_neonatal_death_with_it()
    {
        // A perinatal death is the record least able to be recreated and the
        // one a Ministry most needs counted.
        GivenANeonatalDeath();

        using var db = new NcbrsDbContext(_options);

        db.Registrars.Remove(db.Registrars.Single());

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());

        using var check = new NcbrsDbContext(_options);
        Assert.Single(check.NeonatalOutcomes);
    }

    [Fact]
    public void Deleting_a_birth_record_cannot_take_its_outcome_with_it()
    {
        // A birth record is never deleted -- an annulment keeps it, and the
        // BRN keeps resolving to an explanation. The schema now refuses
        // rather than quietly taking the death record with it.
        GivenANeonatalDeath();

        using var db = new NcbrsDbContext(_options);

        db.BirthRecords.Remove(db.BirthRecords.Single());

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());

        using var check = new NcbrsDbContext(_options);
        Assert.Single(check.NeonatalOutcomes);
    }

    private void GivenANeonatalDeath()
    {
        using var seed = new NcbrsDbContext(_options);

        seed.NeonatalOutcomes.Add(new NeonatalOutcome
        {
            BirthRecordId = seed.BirthRecords.Single().BirthRecordId,
            DeathDateUtc = Born.AddDays(3),
            IcdPmTiming = IcdPmTiming.Neonatal,
            IcdPmCauseCode = "N1",
            RecordedByRegistrarId = RegistrarId,
        });

        seed.SaveChanges();
    }

    private void AssertTheBirthSurvived()
    {
        using var check = new NcbrsDbContext(_options);

        Assert.Equal(Brn, check.BirthRecords.Single().Brn);
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
