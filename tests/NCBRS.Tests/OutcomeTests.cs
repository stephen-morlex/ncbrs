using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the WHO/UN rules the outcome endpoints enforce. These aren't local
/// preferences -- a neonatal death is defined as one within 28 days of a
/// LIVE birth, and a maternal death as one within 42 days of the end of
/// pregnancy. Filing an event outside those windows under these codes would
/// corrupt the national statistics the registry exists to produce.
/// </summary>
public class OutcomeTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid OtherFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly DateTime BirthDate = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

    private const string LiveBirthBrn = "100001";
    private const string FetalDeathBrn = "100002";
    private const string OtherFacilityBrn = "200001";

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public OutcomeTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.AddRange(
            new Facility { FacilityId = FacilityId, Name = "Kabwe", DistrictId = "D-CENTRAL-07" },
            new Facility { FacilityId = OtherFacilityId, Name = "Lusaka", DistrictId = "D-LUSAKA-01" });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Banda",
            CredentialHash = "test"
        });

        db.BirthRecords.AddRange(
            BirthRecordFor(LiveBirthBrn, FacilityId, VitalEventType.LiveBirth),
            BirthRecordFor(FetalDeathBrn, FacilityId, VitalEventType.FetalDeath),
            BirthRecordFor(OtherFacilityBrn, OtherFacilityId, VitalEventType.LiveBirth));

        db.SaveChanges();
    }

    private static BirthRecord BirthRecordFor(string brn, Guid facilityId, VitalEventType type)
    {
        var child = new Person { FullName = $"Child {brn}" };

        return new BirthRecord
        {
            Brn = brn,
            VitalEventType = type,
            ChildPerson = child,
            FacilityId = facilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = BirthDate,
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton
        };
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private static (OutcomeService Service, Registrar Registrar) Build(NcbrsDbContext db, params string[] roles)
    {
        var http = AuthTestContext.HttpContextFor(roles: roles);
        var currentRegistrar = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        return (new OutcomeService(db, new NoOpEventPublisher(), currentRegistrar, new DistrictLookup(db)), registrar);
    }

    private static RecordNeonatalOutcomeRequest Neonatal(int daysAfterBirth, IcdPmTiming timing = IcdPmTiming.Neonatal)
        => new()
        {
            DeathDateUtc = BirthDate.AddDays(daysAfterBirth),
            IcdPmTiming = timing,
            IcdPmCauseCode = "N3",
            ContributingMaternalConditionCode = "M1",
            DeviceId = "TABLET-07"
        };

    private static RecordMaternalOutcomeRequest Maternal(int daysAfterBirth)
        => new()
        {
            DeathDateUtc = BirthDate.AddDays(daysAfterBirth),
            IcdMmCauseCode = "M2",
            DeviceId = "TABLET-07"
        };

    private async Task<OutcomeOutcome<NeonatalOutcomeResponse>> RecordNeonatalAsync(
        string brn, RecordNeonatalOutcomeRequest request, params string[] roles)
    {
        await using var db = NewDb();
        var (service, registrar) = Build(db, roles);
        return await service.RecordNeonatalAsync(brn, request, registrar, Guid.CreateVersion7());
    }

    private async Task<OutcomeOutcome<MaternalOutcomeResponse>> RecordMaternalAsync(
        string brn, RecordMaternalOutcomeRequest request, params string[] roles)
    {
        await using var db = NewDb();
        var (service, registrar) = Build(db, roles);
        return await service.RecordMaternalAsync(brn, request, registrar, Guid.CreateVersion7());
    }

    [Fact]
    public async Task ANeonatalDeath_IsRecordedAsASeparateRecord_NotAnEditToTheBirth()
    {
        var result = await RecordNeonatalAsync(LiveBirthBrn, Neonatal(daysAfterBirth: 5));

        Assert.True(result.Succeeded);

        await using var db = NewDb();
        var outcome = await db.NeonatalOutcomes.SingleAsync();
        var birth = await db.BirthRecords.SingleAsync(r => r.Brn == LiveBirthBrn);

        Assert.Equal(birth.BirthRecordId, outcome.BirthRecordId);
        Assert.Equal("N3", outcome.IcdPmCauseCode);

        // The birth record itself is untouched -- still a live birth.
        Assert.Equal(VitalEventType.LiveBirth, birth.VitalEventType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(28)]
    public async Task ADeathInsideThe28DayWindow_IsAccepted(int days)
        => Assert.True((await RecordNeonatalAsync(LiveBirthBrn, Neonatal(days))).Succeeded);

    [Theory]
    [InlineData(29)]
    [InlineData(120)]
    public async Task ADeathOutsideThe28DayWindow_IsNotANeonatalDeath(int days)
    {
        var result = await RecordNeonatalAsync(LiveBirthBrn, Neonatal(days));

        Assert.Equal(OutcomeResult.Rejected, result.Result);
        Assert.Contains("28-day", result.Detail);
    }

    [Fact]
    public async Task ADeathBeforeTheBirth_IsRejected()
    {
        var result = await RecordNeonatalAsync(LiveBirthBrn, Neonatal(daysAfterBirth: -1));

        Assert.Equal(OutcomeResult.Rejected, result.Result);
    }

    /// <summary>
    /// A fetal death was never born alive, so attaching a neonatal outcome
    /// would assert a live birth that never happened.
    /// </summary>
    [Fact]
    public async Task ANeonatalOutcome_CannotFollowAFetalDeath()
    {
        var result = await RecordNeonatalAsync(FetalDeathBrn, Neonatal(daysAfterBirth: 3));

        Assert.Equal(OutcomeResult.Rejected, result.Result);
        Assert.Contains("live birth", result.Detail);
    }

    /// <summary>
    /// Antepartum and Intrapartum classify stillbirths under ICD-PM. A death
    /// following a live birth is Neonatal by definition, so anything else is
    /// a miscoding.
    /// </summary>
    [Theory]
    [InlineData(IcdPmTiming.Antepartum)]
    [InlineData(IcdPmTiming.Intrapartum)]
    public async Task AStillbirthTimingCode_IsRejectedForALiveBirth(IcdPmTiming timing)
    {
        var result = await RecordNeonatalAsync(LiveBirthBrn, Neonatal(3, timing));

        Assert.Equal(OutcomeResult.Rejected, result.Result);
        Assert.Contains("stillbirth", result.Detail);
    }

    [Fact]
    public async Task RecordingANeonatalOutcomeTwice_IsAConflict()
    {
        await RecordNeonatalAsync(LiveBirthBrn, Neonatal(3));

        var second = await RecordNeonatalAsync(LiveBirthBrn, Neonatal(4));

        Assert.Equal(OutcomeResult.AlreadyRecorded, second.Result);

        await using var db = NewDb();
        Assert.Equal(1, await db.NeonatalOutcomes.CountAsync());
    }

    [Fact]
    public async Task AnUnknownBrn_IsNotFound()
        => Assert.Equal(
            OutcomeResult.BirthRecordNotFound,
            (await RecordNeonatalAsync("NO-SUCH-BRN", Neonatal(3))).Result);

    [Fact]
    public async Task ARecordAtAnotherFacility_IsRefused()
        => Assert.Equal(
            OutcomeResult.NotPermitted,
            (await RecordNeonatalAsync(OtherFacilityBrn, Neonatal(3))).Result);

    [Fact]
    public async Task AMinistryAdmin_MayRecordAcrossFacilities()
        => Assert.True(
            (await RecordNeonatalAsync(OtherFacilityBrn, Neonatal(3), NcbrsRoles.MinistryAdmin)).Succeeded);

    [Fact]
    public async Task ARecordedOutcome_IsAudited()
    {
        await RecordNeonatalAsync(LiveBirthBrn, Neonatal(3));

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(log => log.Action == "RecordNeonatalOutcome");

        Assert.Equal(LiveBirthBrn, audit.EntityId);
        Assert.Equal(RegistrarId, audit.UserId);
        Assert.NotNull(audit.TransactionId);
    }

    // --- maternal -------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    [InlineData(42)]
    public async Task AMaternalDeathInsideThe42DayWindow_IsAccepted(int days)
        => Assert.True((await RecordMaternalAsync(LiveBirthBrn, Maternal(days))).Succeeded);

    [Fact]
    public async Task AMaternalDeathBeyond42Days_IsALateMaternalDeath_NotThis()
    {
        var result = await RecordMaternalAsync(LiveBirthBrn, Maternal(daysAfterBirth: 43));

        Assert.Equal(OutcomeResult.Rejected, result.Result);
        Assert.Contains("late maternal death", result.Detail);
    }

    /// <summary>
    /// The mother's outcome is independent of the child's, so a maternal
    /// death attaches to a fetal death registration too.
    /// </summary>
    [Fact]
    public async Task AMaternalOutcome_MayFollowAFetalDeath()
        => Assert.True((await RecordMaternalAsync(FetalDeathBrn, Maternal(10))).Succeeded);

    [Fact]
    public async Task BothOutcomes_CanCoexistOnOneBirthEvent()
    {
        await RecordNeonatalAsync(LiveBirthBrn, Neonatal(3));
        await RecordMaternalAsync(LiveBirthBrn, Maternal(5));

        await using var db = NewDb();
        Assert.Equal(1, await db.NeonatalOutcomes.CountAsync());
        Assert.Equal(1, await db.MaternalOutcomes.CountAsync());
    }
}
