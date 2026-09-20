using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// How long each kind of facility may go quiet before it is a problem.
///
/// **One threshold across the fleet would be wrong in both directions**, and
/// that is the whole design here. A village post on `OfflineFirst` routinely
/// goes a fortnight without connectivity — alerting at three days buries its
/// district in notifications about posts working exactly as designed, and a
/// queue that is mostly noise is a queue people stop opening. A hospital on
/// `AlwaysOn` silent for three days is already a fault, and waiting a
/// fortnight to say so means a fortnight of births nobody has.
///
/// So the threshold follows <see cref="ConnectivityProfile"/>, which the
/// registry already records per facility.
/// </summary>
public class DeviceSilenceOptions
{
    public const string SectionName = "DeviceSilence";

    /// <summary>Connected facilities: silence is a fault almost immediately.</summary>
    public int AlwaysOnDays { get; set; } = 2;

    public int IntermittentDays { get; set; } = 7;

    /// <summary>
    /// Village posts. Long enough that a normal offline stretch does not
    /// alert, short enough that a dead tablet is found within a reporting
    /// period.
    /// </summary>
    public int OfflineFirstDays { get; set; } = 21;

    /// <summary>
    /// How long after enrolment a device that has never reported is treated
    /// as a failed deployment rather than one not yet unpacked.
    /// </summary>
    public int NeverReportedGraceDays { get; set; } = 7;

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    public int ThresholdFor(ConnectivityProfile profile) => profile switch
    {
        ConnectivityProfile.AlwaysOn => AlwaysOnDays,
        ConnectivityProfile.Intermittent => IntermittentDays,
        _ => OfflineFirstDays
    };
}

public record SilenceSweepResult(int Raised, int Resolved);

/// <summary>
/// Raises an alert against a device that has stopped reporting (plan F4).
///
/// The plan's note is the justification: "a silent device is
/// indistinguishable from a district with no births, and only one of those
/// needs intervention". A post whose tablet died simply stops producing
/// registrations, and every dashboard reports that as a quiet month.
///
/// This runs against the **registry**, not the reporting projection. The
/// projection can only see devices it has heard from, so it structurally
/// cannot report a device that has never sent anything — which is the worst
/// case, not an edge case. Enrolment (WS-B9) is what made that visible.
/// </summary>
public class DeviceSilenceMonitor(
    NcbrsDbContext db,
    DeviceSilenceOptions options,
    TimeProvider clock,
    ILogger<DeviceSilenceMonitor> logger)
{
    public async Task<SilenceSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        var devices = await db.Devices
            .Include(device => device.Facility)
            // Suspended and revoked devices are meant to be silent. Alerting
            // on a tablet a district deliberately withdrew would punish the
            // reporting it is trying to encourage.
            .Where(device => device.Status == DeviceStatus.Enrolled)
            .ToListAsync(cancellationToken);

        var open = await db.DeviceAlerts
            .Where(alert => alert.ResolvedAtUtc == null)
            .ToDictionaryAsync(alert => alert.DeviceId, cancellationToken);

        var raised = 0;
        var resolved = 0;

        foreach (var device in devices)
        {
            var silence = SilenceOf(device, now);

            if (silence is null)
            {
                // Reporting normally. An outstanding alert for it is over --
                // resolution is the device coming back, never an officer
                // saying so.
                if (open.TryGetValue(device.DeviceId, out var stale))
                {
                    stale.Status = DeviceAlertStatus.Resolved;
                    stale.ResolvedAtUtc = now;
                    resolved++;
                }

                continue;
            }

            if (open.ContainsKey(device.DeviceId))
            {
                // Already raised. Not re-raised and not escalated on each
                // sweep: the fact has not changed, and a queue that grows a
                // row every six hours for one dead tablet is a queue nobody
                // opens.
                continue;
            }

            db.DeviceAlerts.Add(new DeviceAlert
            {
                DeviceId = device.DeviceId,
                FacilityId = device.FacilityId,
                DistrictId = device.Facility?.CountyCode ?? "unknown",
                Kind = silence.Kind,
                RaisedAtUtc = now,
                LastSeenAtUtc = device.LastSeenAtUtc,
                DaysSilentWhenRaised = silence.Days,
                ThresholdDays = silence.ThresholdDays
            });

            raised++;
        }

        if (raised > 0 || resolved > 0)
        {
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Device silence sweep raised {Raised} alert(s) and resolved {Resolved}", raised, resolved);
        }

        return new SilenceSweepResult(raised, resolved);
    }

    private record Silence(DeviceAlertKind Kind, int Days, int ThresholdDays);

    private Silence? SilenceOf(Device device, DateTime now)
    {
        if (device.LastSeenAtUtc is not { } lastSeen)
        {
            // Never reported. Measured from enrolment, with a grace period --
            // a tablet enrolled this morning and not yet unpacked is not a
            // failure.
            var sinceEnrolment = (int)(now - device.EnrolledAtUtc).TotalDays;

            return sinceEnrolment >= options.NeverReportedGraceDays
                ? new Silence(DeviceAlertKind.NeverReported, sinceEnrolment, options.NeverReportedGraceDays)
                : null;
        }

        var threshold = options.ThresholdFor(device.Facility?.ConnectivityProfile ?? ConnectivityProfile.OfflineFirst);
        var silentDays = (int)(now - lastSeen).TotalDays;

        return silentDays >= threshold
            ? new Silence(DeviceAlertKind.Silent, silentDays, threshold)
            : null;
    }
}

/// <summary>
/// Runs the sweep on a timer. Separate from the monitor so the detection
/// logic can be exercised directly against a fixed clock rather than by
/// waiting for a background service to tick.
/// </summary>
public class DeviceSilenceSweepService(
    IServiceScopeFactory scopeFactory,
    DeviceSilenceOptions options,
    ILogger<DeviceSilenceSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SweepInterval);

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();

                await scope.ServiceProvider
                    .GetRequiredService<DeviceSilenceMonitor>()
                    .SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                // Monitoring must never take the registration API down with
                // it -- the failure mode this exists to catch is worse than
                // the one it would cause.
                logger.LogError(ex, "Device silence sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
