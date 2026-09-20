using NCBRS.Models;

namespace NCBRS.Client.Sync;

/// <summary>
/// What the centre said became of an uploaded batch, resolved against the
/// outbox: which entries were settled and removed, which were rejected and kept
/// for attention, and how many remain queued.
/// </summary>
public sealed record OutboxSettlement(
    IReadOnlyList<SyncRecordOutcome> Settled,
    IReadOnlyList<SyncRecordOutcome> Rejected,
    int RemainingCount);

/// <summary>
/// WS-B6. The device's local outbox: births registered while offline, staged
/// until connectivity returns, uploaded in one batch, and reconciled against
/// the centre's per-record answer.
///
/// The rule that matters is at <see cref="Settle"/>: a partially rejected batch
/// must leave <em>exactly</em> the rejected records queued. A record the centre
/// registered — or already had (a <see cref="SyncRecordStatus.Duplicate"/>, the
/// expected outcome when a device re-uploads a batch whose response it never
/// saw) — is settled and dropped. Only a genuine rejection stays, so a retry
/// re-sends the rejected records and nothing else, and a lost response costs a
/// harmless re-upload rather than a lost or doubled birth.
///
/// Keyed by BRN throughout (the identifier the device generated, real or
/// <c>PROV-</c>), which is what lets the outbox mark precisely which entries a
/// response settles. A pure state machine: the caller persists <see cref="Pending"/>
/// to the local encrypted store (B2).
/// </summary>
public sealed class SyncOutbox
{
    private readonly string _deviceId;
    private readonly Guid _facilityId;
    private readonly List<SyncBirthRecord> _pending;

    public SyncOutbox(string deviceId, Guid facilityId, IEnumerable<SyncBirthRecord>? restore = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _facilityId = facilityId;
        _pending = restore is null ? [] : [.. restore];
    }

    public IReadOnlyList<SyncBirthRecord> Pending => _pending;

    public int Count => _pending.Count;

    /// <summary>
    /// Stage a registration. Idempotent by BRN: enqueuing the same number twice
    /// (a form re-saved, a restart mid-write) keeps the latest rather than
    /// queuing it again, so a batch never carries a BRN twice.
    /// </summary>
    public void Enqueue(SyncBirthRecord record)
    {
        var brn = record.Birth.Brn;
        if (string.IsNullOrWhiteSpace(brn))
        {
            throw new ArgumentException("A staged record must carry the BRN the device generated.", nameof(record));
        }

        var existing = _pending.FindIndex(entry => entry.Birth.Brn == brn);
        if (existing >= 0)
        {
            _pending[existing] = record;
        }
        else
        {
            _pending.Add(record);
        }
    }

    /// <summary>The upload of everything currently queued, in one call.</summary>
    public SyncBatchRequest BuildBatch()
        => new() { DeviceId = _deviceId, FacilityId = _facilityId, Records = [.. _pending] };

    /// <summary>
    /// Apply the centre's response. Removes every record it registered or
    /// already had; leaves every rejected one — and anything the response does
    /// not mention (a record staged after the batch was built) — queued.
    /// </summary>
    public OutboxSettlement Settle(SyncBatchResponse response)
    {
        var settled = new List<SyncRecordOutcome>();
        var rejected = new List<SyncRecordOutcome>();

        foreach (var outcome in response.Records)
        {
            switch (outcome.Status)
            {
                case SyncRecordStatus.Registered:
                case SyncRecordStatus.Duplicate:
                    if (_pending.RemoveAll(entry => entry.Birth.Brn == outcome.Brn) > 0)
                    {
                        settled.Add(outcome);
                    }

                    break;

                case SyncRecordStatus.Rejected:
                    rejected.Add(outcome);
                    break;
            }
        }

        return new OutboxSettlement(settled, rejected, _pending.Count);
    }
}
