using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Devices that have stopped reporting (plan F4).
///
/// The plan's own note is the justification: "a silent device is
/// indistinguishable from a district with no births, and only one of those
/// needs intervention". A post whose tablet died simply stops producing
/// registrations, and every dashboard reports that as a quiet month. The
/// births still happened — nobody has them.
/// </summary>
public class DeviceSilenceTests : IDisposable
{
    private static readonly DateTime Now = new(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid PostId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid HospitalId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly TimeProvider _clock = new FixedClock(Now);

    public DeviceSilenceTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.AddRange(
            new Facility
            {
                FacilityId = PostId,
                Name = "Terekeka Village Health Post",
                CountyCode = "SS-CE-TER",
                Tier = FacilityTier.VillageHealthPost,
                ConnectivityProfile = ConnectivityProfile.OfflineFirst,
                BrnBlockStart = 100_000,
                BrnBlockEnd = 199_999,
                BrnBlockNextAvailable = 100_000
            },
            new Facility
            {
                FacilityId = HospitalId,
                Name = "Juba Central Hospital",
                CountyCode = "SS-CE-JUB",
                Tier = FacilityTier.Hospital,
                ConnectivityProfile = ConnectivityProfile.AlwaysOn,
                BrnBlockStart = 200_000,
                BrnBlockEnd = 299_999,
                BrnBlockNextAvailable = 200_000
            });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = PostId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "D. Officer",
            Role = RegistrarRole.DistrictOfficer
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private DeviceSilenceMonitor Monitor(NcbrsDbContext db, DeviceSilenceOptions? options = null)
        => new(db, options ?? new DeviceSilenceOptions(), _clock,
            NullLogger<DeviceSilenceMonitor>.Instance);

    private async Task GivenDeviceAsync(
        string deviceId = "TABLET-07",
        Guid? facilityId = null,
        int? lastSeenDaysAgo = 1,
        int enrolledDaysAgo = 60,
        DeviceStatus status = DeviceStatus.Enrolled)
    {
        await using var db = NewDb();

        db.Devices.Add(new Device
        {
            DeviceId = deviceId,
            FacilityId = facilityId ?? PostId,
            PublicKeyPem = DeviceTestKeys.PublicKeyPem,
            Status = status,
            EnrolledAtUtc = Now.AddDays(-enrolledDaysAgo),
            EnrolledByRegistrarId = RegistrarId,
            LastSeenAtUtc = lastSeenDaysAgo is { } days ? Now.AddDays(-days) : null
        });

        await db.SaveChangesAsync();
    }

    private async Task<SilenceSweepResult> SweepAsync(DeviceSilenceOptions? options = null)
    {
        await using var db = NewDb();

        return await Monitor(db, options).SweepAsync();
    }

    private async Task<List<DeviceAlert>> AlertsAsync()
    {
        await using var db = NewDb();

        return await db.DeviceAlerts.ToListAsync();
    }

    // --- thresholds follow connectivity -------------------------------------------

    /// <summary>
    /// The core of the design. One threshold across the fleet is wrong in
    /// both directions: at three days a village post floods its district with
    /// alerts about working exactly as designed, and at three weeks a
    /// hospital's dead terminal costs three weeks of births.
    /// </summary>
    [Fact]
    public async Task AVillagePostAndAHospitalAreJudgedByDifferentThresholds()
    {
        // Ten days of silence: normal for an offline-first post, a fault for
        // a hospital on a permanent connection.
        await GivenDeviceAsync("TABLET-POST", PostId, lastSeenDaysAgo: 10);
        await GivenDeviceAsync("TERMINAL-HOSPITAL", HospitalId, lastSeenDaysAgo: 10);

        await SweepAsync();

        var alert = Assert.Single(await AlertsAsync());

        Assert.Equal("TERMINAL-HOSPITAL", alert.DeviceId);
        Assert.Equal(2, alert.ThresholdDays);
    }

    [Fact]
    public async Task AVillagePostSilentPastItsOwnThresholdIsAlerted()
    {
        await GivenDeviceAsync("TABLET-POST", PostId, lastSeenDaysAgo: 30);

        await SweepAsync();

        var alert = Assert.Single(await AlertsAsync());

        Assert.Equal(DeviceAlertKind.Silent, alert.Kind);
        Assert.Equal(30, alert.DaysSilentWhenRaised);
        Assert.Equal(21, alert.ThresholdDays);
        Assert.Equal("SS-CE-TER", alert.DistrictId);
    }

    [Fact]
    public async Task ADeviceReportingNormallyRaisesNothing()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 1);

        await SweepAsync();

        Assert.Empty(await AlertsAsync());
    }

    // --- never reported ---------------------------------------------------------------

    /// <summary>
    /// Its own kind, because the remedy differs. A device that used to sync
    /// and stopped is a link or a battery; one that has never synced is a
    /// deployment that failed on handover. Folding them together sends an
    /// officer to diagnose a network problem that was never the problem.
    /// </summary>
    [Fact]
    public async Task ADeviceThatHasNeverReportedIsItsOwnKindOfAlert()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: null, enrolledDaysAgo: 30);

        await SweepAsync();

        var alert = Assert.Single(await AlertsAsync());

        Assert.Equal(DeviceAlertKind.NeverReported, alert.Kind);
        Assert.Null(alert.LastSeenAtUtc);
        Assert.Equal(30, alert.DaysSilentWhenRaised);
    }

    /// <summary>
    /// A tablet enrolled this morning and not yet unpacked is not a failure.
    /// </summary>
    [Fact]
    public async Task ADeviceEnrolledTodayIsNotYetAFailedDeployment()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: null, enrolledDaysAgo: 1);

        await SweepAsync();

        Assert.Empty(await AlertsAsync());
    }

    /// <summary>
    /// This is the case the reporting projection structurally cannot see: it
    /// only knows devices it has heard from. The registry knows the device
    /// exists, which is why the sweep runs here.
    /// </summary>
    [Fact]
    public async Task ADeviceThatNeverReportedIsVisibleEvenThoughNoEventMentionsIt()
    {
        await GivenDeviceAsync("TABLET-NEVER", lastSeenDaysAgo: null, enrolledDaysAgo: 40);

        await SweepAsync();

        Assert.Equal("TABLET-NEVER", Assert.Single(await AlertsAsync()).DeviceId);
    }

    // --- not everything silent is a problem ---------------------------------------------

    /// <summary>
    /// A withdrawn tablet is meant to be silent. Alerting on it would punish
    /// the reporting the district is being asked to do.
    /// </summary>
    [Theory]
    [InlineData(DeviceStatus.Suspended)]
    [InlineData(DeviceStatus.Revoked)]
    public async Task ADeviceDeliberatelyWithdrawnIsNotAlertedOn(DeviceStatus status)
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 90, status: status);

        await SweepAsync();

        Assert.Empty(await AlertsAsync());
    }

    // --- the queue stays readable ---------------------------------------------------------

    /// <summary>
    /// The sweep runs every few hours. If each run re-raised the same fact,
    /// one dead tablet would produce four rows a day and the queue would be
    /// unreadable within a week.
    /// </summary>
    [Fact]
    public async Task RepeatedSweepsDoNotReRaiseTheSameAlert()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);

        await SweepAsync();
        await SweepAsync();
        await SweepAsync();

        Assert.Single(await AlertsAsync());
    }

    [Fact]
    public async Task TheSweepReportsWhatItDid()
    {
        await GivenDeviceAsync("TABLET-A", lastSeenDaysAgo: 30);
        await GivenDeviceAsync("TABLET-B", lastSeenDaysAgo: 40);

        Assert.Equal(2, (await SweepAsync()).Raised);
        Assert.Equal(0, (await SweepAsync()).Raised);
    }

    // --- resolution ---------------------------------------------------------------------------

    /// <summary>
    /// Resolution is the device coming back, and only that.
    /// </summary>
    [Fact]
    public async Task AnAlertResolvesWhenTheDeviceReportsAgain()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);
        await SweepAsync();

        await using (var db = NewDb())
        {
            (await db.Devices.SingleAsync()).LastSeenAtUtc = Now.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var result = await SweepAsync();

        Assert.Equal(1, result.Resolved);

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(DeviceAlertStatus.Resolved, alert.Status);
        Assert.NotNull(alert.ResolvedAtUtc);
    }

    /// <summary>
    /// A resolved alert is kept, not deleted. Which posts keep going dark is
    /// the signal behind replacing hardware rather than rebooting it.
    /// </summary>
    [Fact]
    public async Task APostThatKeepsGoingDarkAccumulatesHistory()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);
        await SweepAsync();

        await using (var back = NewDb())
        {
            (await back.Devices.SingleAsync()).LastSeenAtUtc = Now.AddHours(-1);
            await back.SaveChangesAsync();
        }

        await SweepAsync();

        // Dark again.
        await using (var dark = NewDb())
        {
            (await dark.Devices.SingleAsync()).LastSeenAtUtc = Now.AddDays(-40);
            await dark.SaveChangesAsync();
        }

        await SweepAsync();

        var alerts = await AlertsAsync();

        Assert.Equal(2, alerts.Count);
        Assert.Single(alerts, alert => alert.ResolvedAtUtc == null);
    }

    /// <summary>
    /// A district officer looking at the queue minutes after a post came back
    /// should not still be told to drive out there — so a sync resolves the
    /// alert immediately rather than at the next sweep.
    /// </summary>
    [Fact]
    public async Task SyncingResolvesAnOutstandingAlertImmediately()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);
        await SweepAsync();

        await using var db = NewDb();
        var device = await db.Devices.SingleAsync();

        await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions()).MarkSeenAsync(device);
        await db.SaveChangesAsync();

        Assert.Equal(DeviceAlertStatus.Resolved, (await AlertsAsync()).Single().Status);
    }

    // --- acknowledging is not resolving ------------------------------------------------------

    /// <summary>
    /// The distinction that keeps the queue honest. If acknowledging closed
    /// an alert, a district could empty its queue without a single device
    /// coming back — which is precisely the reporting gap this exists to
    /// surface.
    /// </summary>
    [Fact]
    public async Task AcknowledgingAnAlertDoesNotResolveIt()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);
        await SweepAsync();

        var id = (await AlertsAsync()).Single().DeviceAlertId;

        await using var db = NewDb();

        var result = await Devices(db).AcknowledgeAlert(
            id, new ApiRequest<AcknowledgeDeviceAlertRequest> { Data = new() { Note = "Driving out Thursday." } });

        Assert.Null(result.Result);

        var alert = (await AlertsAsync()).Single();

        Assert.Equal(DeviceAlertStatus.Acknowledged, alert.Status);
        Assert.Null(alert.ResolvedAtUtc);
        Assert.Equal("Driving out Thursday.", alert.AcknowledgementNote);
    }

    /// <summary>
    /// An acknowledged alert must not be re-raised by the next sweep, or the
    /// officer's note is lost every six hours.
    /// </summary>
    [Fact]
    public async Task AnAcknowledgedAlertSurvivesTheNextSweep()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);
        await SweepAsync();

        var id = (await AlertsAsync()).Single().DeviceAlertId;

        await using (var db = NewDb())
        {
            await Devices(db).AcknowledgeAlert(
                id, new ApiRequest<AcknowledgeDeviceAlertRequest> { Data = new() { Note = "Known." } });
        }

        await SweepAsync();

        var alert = Assert.Single(await AlertsAsync());

        Assert.Equal(DeviceAlertStatus.Acknowledged, alert.Status);
        Assert.Equal("Known.", alert.AcknowledgementNote);
    }

    [Fact]
    public async Task AcknowledgingAResolvedAlertIsRefused()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);
        await SweepAsync();

        var id = (await AlertsAsync()).Single().DeviceAlertId;

        await using (var back = NewDb())
        {
            (await back.Devices.SingleAsync()).LastSeenAtUtc = Now.AddHours(-1);
            await back.SaveChangesAsync();
        }

        await SweepAsync();

        await using var db = NewDb();

        var result = await Devices(db).AcknowledgeAlert(
            id, new ApiRequest<AcknowledgeDeviceAlertRequest> { Data = new() });

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, error.StatusCode);
    }

    [Fact]
    public async Task AcknowledgingIsAudited()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);
        await SweepAsync();

        var id = (await AlertsAsync()).Single().DeviceAlertId;

        await using var db = NewDb();

        await Devices(db).AcknowledgeAlert(
            id, new ApiRequest<AcknowledgeDeviceAlertRequest> { Data = new() });

        await using var verify = NewDb();
        Assert.Single(await verify.AuditLogs
            .Where(log => log.Action == "AcknowledgeDeviceAlert")
            .ToListAsync());
    }

    // --- the district's queue ------------------------------------------------------------------

    [Fact]
    public async Task TheQueueIsScopedToADistrictAndOrderedBySilence()
    {
        await GivenDeviceAsync("TABLET-A", PostId, lastSeenDaysAgo: 30);
        await GivenDeviceAsync("TABLET-B", PostId, lastSeenDaysAgo: 60);
        await GivenDeviceAsync("TERMINAL-C", HospitalId, lastSeenDaysAgo: 10);

        await SweepAsync();

        await using var db = NewDb();
        var queue = (await Devices(db).Alerts("SS-CE-TER")).Value!;

        Assert.Equal(2, queue.Count);
        Assert.Equal("TABLET-B", queue[0].DeviceId);
        Assert.Equal("TABLET-A", queue[1].DeviceId);
    }

    [Fact]
    public async Task ResolvedAlertsAreHiddenByDefaultAndAvailableOnRequest()
    {
        await GivenDeviceAsync(lastSeenDaysAgo: 30);
        await SweepAsync();

        await using (var back = NewDb())
        {
            (await back.Devices.SingleAsync()).LastSeenAtUtc = Now.AddHours(-1);
            await back.SaveChangesAsync();
        }

        await SweepAsync();

        await using var db = NewDb();
        var controller = Devices(db);

        Assert.Empty((await controller.Alerts()).Value!);
        Assert.Single((await controller.Alerts(includeResolved: true)).Value!);
    }

    private static DevicesController Devices(NcbrsDbContext db)
    {
        var http = AuthTestContext.HttpContextFor(roles: NcbrsRoles.DistrictOfficer);

        return new DevicesController(db, AuthTestContext.RegistrarService(db, http), new CountyLookup(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }
}
