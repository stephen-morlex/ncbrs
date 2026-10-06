using NCBRS.Models;

namespace NCBRS.Client.Sync;

/// <summary>
/// What the centre said became of an uploaded batch, resolved against the
/// outbox: which entries were settled and removed, which were rejected and kept
/// for attention, and how many remain queued.
/// </summary>
/// <param name="SettledBirths">The births the settled entries removed from the outbox, as they were sent, for the tablet's short history (<see cref="RecentBirths"/>).</param>
public sealed record OutboxSettlement(
    IReadOnlyList<SyncRecordOutcome> Settled,
    IReadOnlyList<SyncRecordOutcome> Rejected,
    int RemainingCount,
    IReadOnlyList<SyncBirthRecord>? SettledBirths = null);

/// <summary>
/// WS-B6. The device's local outbox: births registered while offline, staged
/// until connectivity returns, uploaded in one batch, and reconciled against
/// the centre's per-record answer.
///
/// The rule that matters is at <see cref="Settle"/>: a partially rejected batch
/// must leave <em>exactly</em> the rejected records queued. A record the centre
/// registered — or already had (a <see cref="SyncRecordStatus.Duplicate"/>, the
/// expected outcome when a device re-uploads a batch whose response it never
/// saw) — is settled and dropped. Only a genuine rejection stays, and it stays
/// <em>held</em> with the centre's reasons (<see cref="Refused"/>) until a
/// registrar corrects it: resent unchanged, it would only be refused again. A
/// lost response still costs a harmless re-upload rather than a lost or
/// doubled birth.
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
    private readonly Dictionary<string, IReadOnlyList<ApiError>> _refused;

    public SyncOutbox(
        string deviceId,
        Guid facilityId,
        IEnumerable<SyncBirthRecord>? restore = null,
        IReadOnlyDictionary<string, IReadOnlyList<ApiError>>? refused = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _facilityId = facilityId;
        _pending = restore is null ? [] : [.. restore];
        _refused = refused is null
            ? []
            : refused.Where(entry => _pending.Exists(record => record.Birth.Brn == entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    /// <summary>Every birth not yet settled with the centre, refused ones included.</summary>
    public IReadOnlyList<SyncBirthRecord> Pending => _pending;

    public int Count => _pending.Count;

    /// <summary>
    /// Births the centre refused, with its reasons, held until a registrar
    /// corrects them. Resending one unchanged would be refused again every
    /// window, forever, while the birth sat unregistered.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ApiError>> Refused => _refused;

    /// <summary>What the next upload would carry: everything not held for correction.</summary>
    public int SendableCount => _pending.Count(record => !_refused.ContainsKey(record.Birth.Brn));

    /// <summary>
    /// Replace a refused birth with the registrar's correction, and release it
    /// for the next upload. The BRN and the capture time are kept whatever the
    /// correction says: the number is already on the family's slip, and the
    /// statutory window is measured to the moment of capture, so fixing a typo
    /// must not make a birth late. The facility and device stay the device's.
    /// </summary>
    public void Correct(string brn, RegisterBirthRequest corrected)
    {
        if (!_refused.ContainsKey(brn))
        {
            throw new InvalidOperationException($"{brn} is not a birth the registry refused; only refused births are corrected here.");
        }

        var index = _pending.FindIndex(entry => entry.Birth.Brn == brn);
        var original = _pending[index];
        _pending[index] = original with
        {
            Birth = corrected with
            {
                Brn = original.Birth.Brn,
                FacilityId = original.Birth.FacilityId,
                DeviceId = original.Birth.DeviceId,
                RegisteredAtUtc = original.Birth.RegisteredAtUtc,
            },
        };
        _refused.Remove(brn);
    }

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

    /// <summary>The upload of everything queued and not held for correction, in one call.</summary>
    public SyncBatchRequest BuildBatch()
        => new()
        {
            DeviceId = _deviceId,
            FacilityId = _facilityId,
            Records = [.. _pending.Where(record => !_refused.ContainsKey(record.Birth.Brn))],
        };

    /// <summary>
    /// Apply the centre's response. Removes every record it registered or
    /// already had; holds every rejected one for correction, with its reasons;
    /// leaves anything the response does not mention (a record staged after the
    /// batch was built) queued to send.
    /// </summary>
    public OutboxSettlement Settle(SyncBatchResponse response)
    {
        var settled = new List<SyncRecordOutcome>();
        var rejected = new List<SyncRecordOutcome>();
        var births = new List<SyncBirthRecord>();

        foreach (var outcome in response.Records)
        {
            switch (outcome.Status)
            {
                case SyncRecordStatus.Registered:
                case SyncRecordStatus.Duplicate:
                    births.AddRange(_pending.Where(entry => entry.Birth.Brn == outcome.Brn));
                    if (_pending.RemoveAll(entry => entry.Birth.Brn == outcome.Brn) > 0)
                    {
                        settled.Add(outcome);
                    }

                    _refused.Remove(outcome.Brn);

                    break;

                case SyncRecordStatus.Rejected:
                    rejected.Add(outcome);
                    if (_pending.Exists(entry => entry.Birth.Brn == outcome.Brn))
                    {
                        _refused[outcome.Brn] = outcome.Errors ?? [];
                    }

                    break;
            }
        }

        return new OutboxSettlement(settled, rejected, _pending.Count, births);
    }
}
