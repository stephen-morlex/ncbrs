using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Audit rows recording that a request was <b>refused</b> — which must outlive
/// the request, precisely because nothing else it asked for does.
///
/// A device sends a transaction id with every request, so the idempotency
/// filter wraps its work in a transaction and rolls it back on any non-2xx
/// answer. That is right for the work: a refused sync registered nothing and
/// the client may retry. It is wrong for the refusal itself — an attempted
/// upload from a stolen token is the most interesting thing the endpoint sees,
/// and it was being rolled back with everything else, so the audit trail
/// promised by device enrolment never recorded it when it mattered.
///
/// Only rows recorded <i>here</i> survive the rollback. Keeping every audit row
/// from a failed request would be worse than keeping none: an ordinary
/// action's audit ("RegisterBirth") from a request that then failed would
/// assert something that never happened.
/// </summary>
public class RefusalAudit(NcbrsDbContext db, ILogger<RefusalAudit> logger)
{
    private readonly List<AuditLog> _recorded = [];

    /// <summary>
    /// Records a refusal through the request's own context, so a request with
    /// no enclosing transaction commits it with its next save as before.
    /// </summary>
    public void Record(AuditLog refusal)
    {
        db.AuditLogs.Add(refusal);
        _recorded.Add(refusal);
    }

    /// <summary>
    /// Called by the idempotency filter once it has rolled a failed request
    /// back: writes the refusals again, outside the transaction.
    /// </summary>
    public async Task RestoreAfterRollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_recorded.Count == 0)
        {
            return;
        }

        // After a rollback the change tracker still believes everything the
        // action saved is in the database, and anything it added but never
        // saved is still pending. Clear it, so that only the refusals — and
        // nothing the refused work touched — are written.
        db.ChangeTracker.Clear();

        foreach (var refusal in _recorded)
        {
            db.AuditLogs.Add(refusal);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The answer to the caller is already decided (a refusal); failing
            // to record it must not turn that into a 500. Log it in full so the
            // attempt is at least in the logs.
            foreach (var refusal in _recorded)
            {
                logger.LogError(ex,
                    "Could not persist refusal {Action} for {EntityType} {EntityId} from device {DeviceId} (user {UserId}, transaction {TransactionId})",
                    refusal.Action, refusal.EntityType, refusal.EntityId, refusal.DeviceId, refusal.UserId, refusal.TransactionId);
            }
        }
        finally
        {
            _recorded.Clear();
        }
    }
}
