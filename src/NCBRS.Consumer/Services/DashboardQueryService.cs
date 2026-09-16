using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Models;

namespace NCBRS.Consumer.Services;

/// <summary>
/// Reads the §10 indicators out of the projection (plan F2/F3).
///
/// Everything here is derived at query time from facts keyed by their
/// natural id. Nothing is precomputed into a counter, because a counter is
/// the one shape that cannot survive the at-least-once delivery this
/// consumer is built on -- see <see cref="BirthRecordProjector"/>.
///
/// Two rules the figures depend on, both easy to lose in a refactor:
///
/// Annulled registrations are excluded from every count. An annulment says
/// there was no such birth, so counting one would report a birth the
/// register has withdrawn.
///
/// An indicator whose inputs are unknown reports null and says why. It never
/// reports zero. A Ministry reading 0 neonatal deaths concludes the month
/// went well; a Ministry reading "not available" goes and asks. The whole
/// point of a vital statistics system is that the second thing happens.
/// </summary>
public class DashboardQueryService(ReadModelDbContext db, TimeProvider clock)
{
    /// <summary>
    /// How long after a period closes registrations for it keep arriving.
    /// The statutory window (90 days) plus room for a post that was offline
    /// across it -- a figure is not settled until the late filings are in.
    /// </summary>
    private static readonly TimeSpan SettlingPeriod = TimeSpan.FromDays(120);

    private static readonly string[] Unanswerable =
    [
        "Certificate turnaround (§10): certificate issue is not published to the event stream, "
        + "and 'in the family's hands' is a physical handover the system never observes. "
        + "Issuance-to-print is the closest measurable proxy and would need its own event.",

        "Registration completeness (§10): only timeliness is computed here. True completeness "
        + "needs projected births from a census or demographic model as the denominator, which "
        + "the registry does not hold.",

        "System uptime and district autonomous-operation success (§10): infrastructure telemetry, "
        + "not vital events -- it belongs to monitoring, not to this projection."
    ];

    public async Task<DashboardSummary> SummaryAsync(
        DateTime fromUtc,
        DateTime toUtc,
        string? districtId = null,
        CancellationToken cancellationToken = default)
    {
        // Counted by date of occurrence, per UN P&R Rev. 3 -- not by when the
        // registration reached the centre, which measures connectivity rather
        // than births.
        var inPeriod = await InPeriod(fromUtc, toUtc, districtId).ToListAsync(cancellationToken);

        var births = inPeriod.Where(fact => fact.AnnulledAtUtc is null).ToList();
        var annulled = inPeriod.Count - births.Count;

        var brns = births.Select(fact => fact.Brn).ToList();

        var liveBirths = births.Count(fact => fact.VitalEventType != "FetalDeath");

        var neonatal = await db.NeonatalOutcomeFacts
            .CountAsync(fact => brns.Contains(fact.Brn), cancellationToken);

        var maternal = await db.MaternalOutcomeFacts
            .CountAsync(fact => brns.Contains(fact.Brn), cancellationToken);

        var syncs = await Syncs(fromUtc, toUtc, districtId).ToListAsync(cancellationToken);

        return new DashboardSummary(
            Period(fromUtc, toUtc),
            districtId,
            Counts(births, annulled),
            Window(births),
            Confirmation(births),
            Delay(births),
            MortalityOf(neonatal, maternal, liveBirths),
            SyncOf(syncs),
            DuplicatesOf(syncs, births.Count),
            Unanswerable);
    }

    /// <summary>The district drill-down behind the national view.</summary>
    public async Task<IReadOnlyList<DistrictSummary>> DistrictsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var inPeriod = await InPeriod(fromUtc, toUtc, districtId: null).ToListAsync(cancellationToken);

        return [.. inPeriod
            .GroupBy(fact => fact.DistrictId)
            .Select(group =>
            {
                var births = group.Where(fact => fact.AnnulledAtUtc is null).ToList();

                return new DistrictSummary(
                    group.Key,
                    births.Count(fact => fact.VitalEventType != "FetalDeath"),
                    group.Count() - births.Count,
                    Share(
                        births.Count(fact => fact.WithinStatutoryWindow == true),
                        births.Count(fact => fact.WithinStatutoryWindow.HasValue)));
            })
            .OrderByDescending(summary => summary.LiveBirths)
            .ThenBy(summary => summary.DistrictId)];
    }

    /// <summary>
    /// Devices that have synced before and have not synced lately (plan F4).
    ///
    /// It can only report devices the stream has heard from at least once. A
    /// post that was deployed and never synced at all is invisible here --
    /// the registry knows which facilities exist, this projection does not,
    /// and that silent failure is the one most worth catching. Enrolment
    /// (WS-B9) is what would close it.
    /// </summary>
    public async Task<IReadOnlyList<SilentDevice>> SilentDevicesAsync(
        int silentForDays,
        string? districtId = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-silentForDays);

        var query = db.SyncBatchFacts.AsQueryable();

        if (districtId is not null)
        {
            query = query.Where(fact => fact.DistrictId == districtId);
        }

        var lastSeen = await query
            .GroupBy(fact => fact.DeviceId)
            .Select(group => new
            {
                DeviceId = group.Key,
                LastSyncAtUtc = group.Max(fact => fact.SyncedAtUtc)
            })
            .Where(device => device.LastSyncAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        var silent = new List<SilentDevice>();

        foreach (var device in lastSeen)
        {
            // The batch that fixes the device to a facility and district.
            var latest = await query
                .Where(fact => fact.DeviceId == device.DeviceId
                               && fact.SyncedAtUtc == device.LastSyncAtUtc)
                .FirstAsync(cancellationToken);

            silent.Add(new SilentDevice(
                device.DeviceId,
                latest.DistrictId,
                latest.FacilityId,
                device.LastSyncAtUtc,
                (int)(now - device.LastSyncAtUtc).TotalDays));
        }

        return [.. silent.OrderByDescending(device => device.DaysSilent)];
    }

    /// <summary>
    /// Every registration whose birth falls in the period, annulled ones
    /// included. They are split out immediately by each caller and excluded
    /// from every indicator -- an annulment says there was no such birth --
    /// but they are carried this far so the count of them is real rather
    /// than a zero nobody computed.
    /// </summary>
    private IQueryable<RegistrationFact> InPeriod(DateTime fromUtc, DateTime toUtc, string? districtId)
    {
        var query = db.RegistrationFacts
            .Where(fact => fact.DateOfBirth >= fromUtc && fact.DateOfBirth < toUtc);

        return districtId is null ? query : query.Where(fact => fact.DistrictId == districtId);
    }

    private IQueryable<SyncBatchFact> Syncs(DateTime fromUtc, DateTime toUtc, string? districtId)
    {
        // By sync date, not date of birth: this measures the link, and a
        // batch delivered in September may carry August's births.
        var query = db.SyncBatchFacts
            .Where(fact => fact.SyncedAtUtc >= fromUtc && fact.SyncedAtUtc < toUtc);

        return districtId is null ? query : query.Where(fact => fact.DistrictId == districtId);
    }

    private ReportingPeriod Period(DateTime fromUtc, DateTime toUtc)
        => new(fromUtc, toUtc, clock.GetUtcNow().UtcDateTime - toUtc < SettlingPeriod);

    private static RegistrationCounts Counts(List<RegistrationFact> births, int annulled)
    {
        var live = births.Where(fact => fact.VitalEventType != "FetalDeath").ToList();

        var male = live.Count(fact => fact.Sex == "Male");
        var female = live.Count(fact => fact.Sex == "Female");

        return new RegistrationCounts(
            live.Count,
            births.Count(fact => fact.VitalEventType == "FetalDeath"),
            births.Count(fact => fact.VitalEventType is null),
            annulled,
            male,
            female,
            female == 0 ? null : Round(male * 100m / female));
    }

    private static Timeliness Window(List<RegistrationFact> births)
    {
        var within = births.Count(fact => fact.WithinStatutoryWindow == true);
        var outside = births.Count(fact => fact.WithinStatutoryWindow == false);
        var unknown = births.Count(fact => fact.WithinStatutoryWindow is null);

        return new Timeliness(within, outside, unknown, Share(within, within + outside));
    }

    private static TimeToConfirmation Confirmation(List<RegistrationFact> births)
    {
        var confirmed = births.Where(fact => fact.ConfirmedAtUtc.HasValue).ToList();

        var byTier = confirmed
            .GroupBy(fact => fact.FacilityTier ?? "Unknown")
            .Select(group => new TierTimeToConfirmation(
                group.Key,
                group.Count(),
                Median(group.Select(DaysToConfirmation))))
            .OrderBy(tier => tier.FacilityTier)
            .ToList();

        return new TimeToConfirmation(
            confirmed.Count,
            births.Count - confirmed.Count,
            Median(confirmed.Select(DaysToConfirmation)),
            byTier);
    }

    /// <summary>
    /// Splits the delay between a birth and the centre knowing about it into
    /// the two questions it actually contains: how long the family took to
    /// reach a registrar, and how long the record then took to reach the
    /// centre.
    ///
    /// TimeToConfirmation above spans both and cannot separate them. For a
    /// hospital that hardly matters; for the offline tier the second term can
    /// be most of the total, and reading it as the first would say families
    /// near a village post are slow to register when they are not.
    /// </summary>
    private static RegistrationDelay Delay(List<RegistrationFact> births)
    {
        var measured = births.Where(fact => fact.RegisteredAtUtc.HasValue).ToList();

        var byTier = measured
            .GroupBy(fact => fact.FacilityTier ?? "Unknown")
            .Select(group => new TierRegistrationDelay(
                group.Key,
                group.Count(),
                Median(group.Select(DaysBirthToRegistration)),
                Median(group.Select(DaysRegistrationToCentre))))
            .OrderBy(tier => tier.FacilityTier)
            .ToList();

        return new RegistrationDelay(
            measured.Count,
            births.Count - measured.Count,
            Median(measured.Select(DaysBirthToRegistration)),
            Median(measured.Select(DaysRegistrationToCentre)),
            byTier);
    }

    /// <summary>How long the family took to reach a registrar.</summary>
    private static decimal DaysBirthToRegistration(RegistrationFact fact)
        => (decimal)(fact.RegisteredAtUtc!.Value - fact.DateOfBirth).TotalDays;

    /// <summary>
    /// How long the record then waited for a link.
    ///
    /// Floored at zero rather than allowed negative. A device clock inside the
    /// skew tolerance the registry accepts may be slightly ahead of the
    /// server, so an online registration can publish a fraction of a second
    /// "before" it was registered; a median dragged below zero by that would
    /// report the centre receiving births ahead of them being registered.
    /// </summary>
    private static decimal DaysRegistrationToCentre(RegistrationFact fact)
    {
        var days = (decimal)(fact.PublishedAtUtc - fact.RegisteredAtUtc!.Value).TotalDays;

        return days > 0 ? days : 0;
    }

    private static Mortality MortalityOf(int neonatal, int maternal, int liveBirths)
        => new(
            neonatal,
            maternal,
            liveBirths == 0 ? null : Round(neonatal * 1_000m / liveBirths),
            liveBirths == 0 ? null : Round(maternal * 100_000m / liveBirths));

    private static SyncReliability SyncOf(List<SyncBatchFact> syncs)
    {
        var submitted = syncs.Sum(fact => fact.Submitted);

        return new SyncReliability(
            syncs.Count,
            syncs.Select(fact => fact.DeviceId).Distinct().Count(),
            submitted,
            syncs.Sum(fact => fact.Registered),
            syncs.Sum(fact => fact.Rejected),
            Share(syncs.Sum(fact => fact.Registered), submitted));
    }

    private static DuplicateRate DuplicatesOf(List<SyncBatchFact> syncs, int births)
    {
        var duplicates = syncs.Sum(fact => fact.Duplicates);

        return new DuplicateRate(
            duplicates,
            births == 0 ? null : Round(duplicates * 10_000m / births),
            "Counts duplicates caught on the sync path only. A duplicate refused on a direct "
            + "online registration is not published to any topic, so this is a floor.");
    }

    private static decimal DaysToConfirmation(RegistrationFact fact)
        => (decimal)(fact.ConfirmedAtUtc!.Value - fact.DateOfBirth).TotalDays;

    /// <summary>
    /// The draft asks for medians, not means, and the reason matters: one
    /// record registered eleven years late would drag a mean far enough to
    /// describe nobody's experience.
    /// </summary>
    private static decimal? Median(IEnumerable<decimal> values)
    {
        var ordered = values.OrderBy(value => value).ToList();

        if (ordered.Count == 0)
        {
            return null;
        }

        var middle = ordered.Count / 2;

        return Round(ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2m);
    }

    private static decimal? Share(int part, int whole)
        => whole == 0 ? null : Round(part * 100m / whole);

    private static decimal Round(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
