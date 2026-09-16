using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

public enum ProvisionalReconciliation
{
    /// <summary>A real BRN was assigned from the facility's range.</summary>
    Assigned,

    /// <summary>Nothing to do -- the record already carries a real BRN.</summary>
    NotProvisional,

    /// <summary>
    /// The facility's own pre-approved range is used up centrally, so there
    /// is no number to give. The record keeps its provisional identifier
    /// until the central registry extends the range.
    /// </summary>
    FacilityRangeExhausted
}

public record ProvisionalReconciliationResult(
    ProvisionalReconciliation Outcome,
    string? AssignedBrn = null,
    string? Detail = null)
{
    public bool Assigned => Outcome is ProvisionalReconciliation.Assigned;
}

/// <summary>
/// Turns a provisional identifier into a real BRN once the record reaches
/// the centre (draft 6.3: the fallback sequence "is reconciled -- not
/// silently merged -- on sync").
///
/// Not silently merged means the act is recorded and the provisional
/// identifier is kept, not that a human must press a button: assigning the
/// next number from a range the facility already holds is mechanical, and
/// making a district officer do it by hand would leave families without
/// certificates for no gain. What must never happen is the record quietly
/// entering the register as though it had always had a proper number.
/// </summary>
public class ProvisionalRecordReconciler(NcbrsDbContext db)
{
    /// <summary>
    /// Matches the retry count used when granting BRN blocks: the same
    /// [ConcurrencyCheck] column guards both, and two records reconciling at
    /// once must not be handed the same number.
    /// </summary>
    private const int MaxConcurrencyRetries = 5;

    /// <summary>
    /// Assigns the next free BRN to a record still carrying a provisional
    /// identifier. Saves on success, because the number must not be handed
    /// out twice if the caller's later work fails.
    /// </summary>
    public async Task<ProvisionalReconciliationResult> ReconcileAsync(
        BirthRecord record,
        Guid actingRegistrarId,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        if (record.ProvisionalIdentifier is null || record.ReconciledAtUtc is not null)
        {
            return new ProvisionalReconciliationResult(ProvisionalReconciliation.NotProvisional);
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var facility = await db.Facilities.FirstOrDefaultAsync(
                f => f.FacilityId == record.FacilityId, cancellationToken);

            if (facility is null)
            {
                return new ProvisionalReconciliationResult(
                    ProvisionalReconciliation.FacilityRangeExhausted,
                    Detail: $"Facility '{record.FacilityId}' no longer exists.");
            }

            if (facility.BrnBlockNextAvailable > facility.BrnBlockEnd)
            {
                return new ProvisionalReconciliationResult(
                    ProvisionalReconciliation.FacilityRangeExhausted,
                    Detail: $"Facility '{facility.FacilityId}' has exhausted its pre-approved BRN range "
                            + $"(ceiling {facility.BrnBlockEnd}). Record '{record.ProvisionalIdentifier}' keeps its "
                            + "provisional identifier until the central registry assigns a new range.");
            }

            var assigned = facility.BrnBlockNextAvailable;

            // Taken from the same counter that grants device blocks, so a
            // reconciled record can never be given a number a device is
            // already holding.
            facility.BrnBlockNextAvailable = assigned + 1;

            var brn = assigned.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (await db.BirthRecords.AnyAsync(other => other.Brn == brn, cancellationToken))
            {
                // Something already holds this number despite the counter --
                // skip it rather than collide, and let the loop try the next.
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            record.Brn = brn;
            record.ReconciledAtUtc = DateTime.UtcNow;
            record.ConfirmedAtUtc = record.ReconciledAtUtc;

            if (record.Status == RecordStatus.Provisional)
            {
                record.Status = RecordStatus.Confirmed;
            }

            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(BirthRecord),
                EntityId = brn,
                // Names the identifier it replaced, so the audit trail links
                // the provisional slip to the number that superseded it.
                Action = $"ReconcileProvisional:{record.ProvisionalIdentifier}",
                UserId = actingRegistrarId,
                DeviceId = "reconciliation",
                TransactionId = transactionId
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                return new ProvisionalReconciliationResult(ProvisionalReconciliation.Assigned, brn);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another grant advanced BrnBlockNextAvailable underneath us.
                // Drop what this attempt staged and read fresh.
                foreach (var entry in db.ChangeTracker.Entries().ToList())
                {
                    await entry.ReloadAsync(cancellationToken);
                }
            }
        }

        return new ProvisionalReconciliationResult(
            ProvisionalReconciliation.FacilityRangeExhausted,
            Detail: "Could not assign a BRN after repeated contention. Retry the sync.");
    }
}
