using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// One row per facility of what needs someone's attention (plan §17 item 35):
/// births refused at sync, numbers still provisional or unconfirmed, review
/// queues and how long they have waited, devices gone quiet, and certificates
/// not yet in families' hands.
///
/// <para><b>From the registry, not the reporting projection.</b> The dashboard
/// counts what happened; this counts what is waiting on someone, which only the
/// registry holds. Each figure uses the same definition as the queue it
/// summarises (the certificate figures are <see cref="CertificateQueueService.Outstanding"/>
/// itself), so a row never disagrees with the list it points to.</para>
///
/// <para><b>Unknown is not zero.</b> Batches processed before their outcomes
/// were kept carry no counts, and a window holding only those reports the
/// refusal figures as null rather than as a clean zero.</para>
/// </summary>
public class FacilityOperationsService(NcbrsDbContext db, TimeProvider clock)
{
    public const int WindowDays = 30;

    /// <param name="facilityId">One facility, or null for every facility in scope.</param>
    /// <param name="countyCode">The caller's county, or null for the Ministry. Required, so nobody gets the country by forgetting it.</param>
    public async Task<FacilityOperationsReport> ReportAsync(
        Guid? facilityId, string? countyCode, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-WindowDays);

        var facilitiesQuery = db.Facilities.AsNoTracking();
        if (facilityId is not null)
        {
            facilitiesQuery = facilitiesQuery.Where(facility => facility.FacilityId == facilityId);
        }

        if (countyCode is not null)
        {
            facilitiesQuery = facilitiesQuery.Where(facility => facility.CountyCode == countyCode);
        }

        var facilities = await facilitiesQuery
            .OrderBy(facility => facility.CountyCode).ThenBy(facility => facility.Name)
            .Select(facility => new { facility.FacilityId, facility.Name, facility.CountyCode, facility.Tier, facility.ConnectivityProfile })
            .ToListAsync(cancellationToken);

        var ids = facilities.Select(facility => facility.FacilityId).ToList();
        var records = db.BirthRecords.AsNoTracking().Where(record => ids.Contains(record.FacilityId));

        var registrations = await records
            .Where(record => record.CreatedAtUtc >= since)
            .GroupBy(record => record.FacilityId)
            .Select(group => new { FacilityId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.FacilityId, row => row.Count, cancellationToken);

        var batches = db.SyncBatches.AsNoTracking().Where(batch => ids.Contains(batch.FacilityId));

        var lastSync = await batches
            .GroupBy(batch => batch.FacilityId)
            .Select(group => new { FacilityId = group.Key, At = group.Max(batch => batch.SubmittedAtUtc) })
            .ToDictionaryAsync(row => row.FacilityId, row => row.At, cancellationToken);

        // Only batches that kept their outcomes; a window with none of those is unknown, not clean.
        var outcomes = await batches
            .Where(batch => batch.SubmittedAtUtc >= since && batch.RejectedCount != null)
            .GroupBy(batch => batch.FacilityId)
            .Select(group => new
            {
                FacilityId = group.Key,
                Synced = group.Sum(batch => batch.RecordCount),
                Refused = group.Sum(batch => batch.RejectedCount!.Value),
            })
            .ToDictionaryAsync(row => row.FacilityId, cancellationToken);

        var devices = await db.Devices.AsNoTracking()
            .Where(device => ids.Contains(device.FacilityId) && device.Status == DeviceStatus.Enrolled)
            .GroupBy(device => device.FacilityId)
            .Select(group => new { FacilityId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.FacilityId, row => row.Count, cancellationToken);

        var alerts = await db.DeviceAlerts.AsNoTracking()
            .Where(alert => ids.Contains(alert.FacilityId) && alert.Status != DeviceAlertStatus.Resolved)
            .GroupBy(alert => alert.FacilityId)
            .Select(group => new { FacilityId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.FacilityId, row => row.Count, cancellationToken);

        var live = records.Where(record => record.AnnulledAtUtc == null);

        var provisional = await live
            .Where(record => record.ProvisionalIdentifier != null && record.ReconciledAtUtc == null)
            .GroupBy(record => record.FacilityId)
            .Select(group => new { FacilityId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.FacilityId, row => row.Count, cancellationToken);

        var unconfirmed = await live
            .Where(record => record.ProvisionalIdentifier == null && record.ConfirmedAtUtc == null)
            .GroupBy(record => record.FacilityId)
            .Select(group => new { FacilityId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.FacilityId, row => row.Count, cancellationToken);

        // A correction is one request, however many fields it changes.
        var corrections = (await db.BirthRecordAmendments.AsNoTracking()
                .Where(amendment => amendment.Status == AmendmentStatus.PendingApproval
                                    && ids.Contains(amendment.BirthRecord!.FacilityId))
                .Select(amendment => new { amendment.BirthRecord!.FacilityId, amendment.AmendmentRequestId, amendment.AmendedAtUtc })
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.FacilityId)
            .ToDictionary(
                group => group.Key,
                group => new Backlog(
                    group.Select(row => row.AmendmentRequestId).Distinct().Count(),
                    UtcTime.AsUtc(group.Min(row => row.AmendedAtUtc))));

        var late = await db.LateRegistrations.AsNoTracking()
            .Where(entry => entry.Status == LateRegistrationStatus.PendingApproval
                            && ids.Contains(entry.BirthRecord!.FacilityId))
            .GroupBy(entry => entry.BirthRecord!.FacilityId)
            .Select(group => new { FacilityId = group.Key, Count = group.Count(), Oldest = group.Min(entry => entry.SubmittedAtUtc) })
            .ToDictionaryAsync(row => row.FacilityId, row => new Backlog(row.Count, UtcTime.AsUtc(row.Oldest)), cancellationToken);

        var duplicates = await db.DuplicateCandidates.AsNoTracking()
            .Where(candidate => candidate.Status == DuplicateReviewStatus.Pending
                                && ids.Contains(candidate.BirthRecord!.FacilityId))
            .GroupBy(candidate => candidate.BirthRecord!.FacilityId)
            .Select(group => new { FacilityId = group.Key, Count = group.Count(), Oldest = group.Min(candidate => candidate.DetectedAtUtc) })
            .ToDictionaryAsync(row => row.FacilityId, row => new Backlog(row.Count, UtcTime.AsUtc(row.Oldest)), cancellationToken);

        var outstanding = CertificateQueueService.Outstanding(records);

        var uncollected = await outstanding
            .GroupBy(record => record.FacilityId)
            .Select(group => new { FacilityId = group.Key, Oldest = group.Min(record => record.CreatedAtUtc) })
            .ToDictionaryAsync(row => row.FacilityId, row => UtcTime.AsUtc(row.Oldest), cancellationToken);

        var toIssue = await CountByFacilityAsync(CertificateQueueService.InStage(outstanding, CertificateQueueStage.ToIssue), cancellationToken);
        var toHandOver = await CountByFacilityAsync(CertificateQueueService.InStage(outstanding, CertificateQueueStage.ToHandOver), cancellationToken);

        var none = new Backlog(0, null);

        return new FacilityOperationsReport(
            now,
            WindowDays,
            facilities.Select(facility => new FacilityOperations(
                facility.FacilityId,
                facility.Name,
                facility.CountyCode,
                facility.Tier,
                facility.ConnectivityProfile,
                registrations.GetValueOrDefault(facility.FacilityId),
                lastSync.TryGetValue(facility.FacilityId, out var synced) ? UtcTime.AsUtc(synced) : null,
                devices.GetValueOrDefault(facility.FacilityId),
                alerts.GetValueOrDefault(facility.FacilityId),
                outcomes.TryGetValue(facility.FacilityId, out var known) ? known.Synced : null,
                outcomes.TryGetValue(facility.FacilityId, out known) ? known.Refused : null,
                provisional.GetValueOrDefault(facility.FacilityId),
                unconfirmed.GetValueOrDefault(facility.FacilityId),
                corrections.GetValueOrDefault(facility.FacilityId, none),
                late.GetValueOrDefault(facility.FacilityId, none),
                duplicates.GetValueOrDefault(facility.FacilityId, none),
                toIssue.GetValueOrDefault(facility.FacilityId),
                toHandOver.GetValueOrDefault(facility.FacilityId),
                uncollected.TryGetValue(facility.FacilityId, out var oldest) ? oldest : null))
                .ToList());
    }

    private static Task<Dictionary<Guid, int>> CountByFacilityAsync(IQueryable<BirthRecord> query, CancellationToken cancellationToken)
        => query
            .GroupBy(record => record.FacilityId)
            .Select(group => new { FacilityId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.FacilityId, row => row.Count, cancellationToken);
}
