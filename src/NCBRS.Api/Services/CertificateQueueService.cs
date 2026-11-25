using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Every live birth whose certificate has not reached the family (plan §17 item
/// 32), oldest first.
///
/// <para>At a post offline for weeks, the family leaves with a slip: only the
/// registry signs a certificate, so it is printed later somewhere with signal and
/// has to find its way back. Nothing tracked that journey, so a birth registered
/// and never certified looked exactly like one certified and collected. This is
/// the list a facility and its district work from.</para>
///
/// <para><b>What is on it</b> follows the rules for issuing exactly
/// (<see cref="CertificateService.IssueAsync"/>), so the list never promises a
/// certificate that would be refused. A birth stays on it until its current
/// certificate is handed over; one whose certificate was withdrawn after an
/// amendment comes back, because the paper the family holds no longer stands.</para>
///
/// <para><b>What is not:</b> annulled registrations (there is no birth),
/// stillbirths (never certified), a record superseded as a duplicate (its
/// survivor carries the certificate), and a late registration that was refused
/// (no certificate can be issued against it). Births waiting on the registry,
/// for a number or a verification, are listed with that stage, so a district
/// can see why a family is still waiting.</para>
/// </summary>
public class CertificateQueueService(NcbrsDbContext db)
{
    /// <param name="facilityId">One facility, or null for every facility in scope.</param>
    /// <param name="countyCode">The caller's county, or null for the Ministry. Required, so no caller gets the whole country by forgetting it.</param>
    /// <param name="stage">One stage only, or null for all.</param>
    public async Task<Page<CertificateQueueItem>> ListAsync(
        Guid? facilityId,
        string? countyCode,
        CertificateQueueStage? stage,
        PageRequest paging,
        CancellationToken cancellationToken = default)
    {
        var query = db.BirthRecords
            .AsNoTracking()
            .Where(record => record.AnnulledAtUtc == null
                             && record.VitalEventType == VitalEventType.LiveBirth
                             && record.SupersededByBirthRecordId == null
                             && (record.LateRegistration == null || record.LateRegistration.Status != LateRegistrationStatus.Rejected)
                             && !record.Certificates.Any(c => c.WithdrawnAtUtc == null && c.HandedOverAtUtc != null));

        if (facilityId is not null)
        {
            query = query.Where(record => record.FacilityId == facilityId);
        }

        if (countyCode is not null)
        {
            query = query.Where(record => record.Facility!.CountyCode == countyCode);
        }

        query = stage switch
        {
            CertificateQueueStage.ToHandOver => query.Where(record =>
                record.Certificates.Any(c => c.WithdrawnAtUtc == null)),

            CertificateQueueStage.AwaitingNumber => query.Where(record =>
                !record.Certificates.Any(c => c.WithdrawnAtUtc == null)
                && record.ProvisionalIdentifier != null && record.ReconciledAtUtc == null),

            CertificateQueueStage.AwaitingVerification => query.Where(record =>
                !record.Certificates.Any(c => c.WithdrawnAtUtc == null)
                && !(record.ProvisionalIdentifier != null && record.ReconciledAtUtc == null)
                && record.LateRegistration != null && record.LateRegistration.Status != LateRegistrationStatus.Approved),

            CertificateQueueStage.ToIssue => query.Where(record =>
                !record.Certificates.Any(c => c.WithdrawnAtUtc == null)
                && !(record.ProvisionalIdentifier != null && record.ReconciledAtUtc == null)
                && (record.LateRegistration == null || record.LateRegistration.Status == LateRegistrationStatus.Approved)),

            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);

        if (PageCursor.TryDecode(paging.After, out var cursor) && cursor.IsDateTime())
        {
            var at = cursor.AsDateTime();
            query = query.Where(record =>
                record.CreatedAtUtc > at
                || (record.CreatedAtUtc == at && record.BirthRecordId.CompareTo(cursor.Id) > 0));
        }

        var rows = await query
            .OrderBy(record => record.CreatedAtUtc)
            .ThenBy(record => record.BirthRecordId)
            .Take(paging.EffectiveLimit + 1)
            .Select(record => new
            {
                record.BirthRecordId,
                record.Brn,
                record.ProvisionalIdentifier,
                record.ReconciledAtUtc,
                ChildFullName = record.ChildPerson!.FullName,
                record.DateOfBirth,
                record.FacilityId,
                FacilityName = record.Facility!.Name,
                record.RegisteredAtUtc,
                record.CreatedAtUtc,
                LateStatus = record.LateRegistration == null ? (LateRegistrationStatus?)null : record.LateRegistration.Status,
                IssuedAtUtc = record.Certificates
                    .Where(c => c.WithdrawnAtUtc == null)
                    .Select(c => (DateTime?)c.IssueDateUtc)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(row => new
            {
                row.CreatedAtUtc,
                Item = new CertificateQueueItem(
                    row.BirthRecordId,
                    row.Brn,
                    row.ChildFullName,
                    UtcTime.AsUtc(row.DateOfBirth),
                    row.FacilityId,
                    row.FacilityName,
                    UtcTime.AsUtc(row.RegisteredAtUtc ?? row.CreatedAtUtc),
                    StageOf(row.IssuedAtUtc, row.ProvisionalIdentifier, row.ReconciledAtUtc, row.LateStatus),
                    row.IssuedAtUtc is { } issued ? UtcTime.AsUtc(issued) : null),
            })
            .ToList();

        var page = Page<CertificateQueueItem>.From(
            items.Select(entry => entry.Item).ToList(), total, paging.EffectiveLimit,
            last => PageCursor.For(items.First(entry => entry.Item.BirthRecordId == last.BirthRecordId).CreatedAtUtc, last.BirthRecordId));

        return page;
    }

    /// <summary>The same order of checks as issuing: a certificate first, then the number, then the verification.</summary>
    private static CertificateQueueStage StageOf(
        DateTime? issuedAtUtc, string? provisionalIdentifier, DateTime? reconciledAtUtc, LateRegistrationStatus? late)
        => issuedAtUtc is not null ? CertificateQueueStage.ToHandOver
            : provisionalIdentifier is not null && reconciledAtUtc is null ? CertificateQueueStage.AwaitingNumber
            : late is not null && late != LateRegistrationStatus.Approved ? CertificateQueueStage.AwaitingVerification
            : CertificateQueueStage.ToIssue;
}
