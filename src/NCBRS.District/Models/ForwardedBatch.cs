namespace NCBRS.District.Models;

public enum ForwardedBatchStatus
{
    /// <summary>Held at the district. The centre has not seen it yet.</summary>
    Queued,

    /// <summary>The centre accepted it and its answer is stored against this row.</summary>
    Forwarded,

    /// <summary>
    /// The centre refused it for a reason retrying will not fix -- a malformed
    /// batch, or one the uploading account is not entitled to file. Held so
    /// the district can see what was rejected and why.
    /// </summary>
    Rejected
}

/// <summary>
/// One facility batch held at the district on its way to the centre
/// (draft 6.2, 7.2).
///
/// The node exists so a village post's sync does not depend on the national
/// tier being reachable at that moment. What it stores is the batch as the
/// device sent it, byte for byte, plus whatever the centre eventually said
/// about it -- nothing is re-derived on the way through, because a node that
/// reinterprets a batch is a second place registration logic can drift.
/// </summary>
public class ForwardedBatch
{
    public Guid ForwardedBatchId { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The caller's transaction id, carried through unchanged.
    ///
    /// This is what makes the hop idempotent end to end: the centre's own
    /// idempotency check keys on it, so a batch forwarded twice -- by a retry
    /// here, or by a device that also reached the centre directly -- is
    /// recognised as the same submission rather than registered twice.
    /// </summary>
    public Guid TransactionId { get; set; }

    public required string DeviceId { get; set; }

    public Guid FacilityId { get; set; }

    public int RecordCount { get; set; }

    /// <summary>
    /// The request envelope exactly as it arrived, replayed verbatim when the
    /// centre is reachable.
    /// </summary>
    public required string Payload { get; set; }

    public ForwardedBatchStatus Status { get; set; } = ForwardedBatchStatus.Queued;

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ForwardedAtUtc { get; set; }

    public int Attempts { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    /// <summary>
    /// When this row may next be tried. Backoff so a node that spent the
    /// night unable to reach the centre is not hammering it at dawn.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// The centre's response body, stored so a device that was told "queued"
    /// can come back later and find out what actually happened to each record
    /// -- which BRNs were confirmed, which were duplicates, which failed.
    /// </summary>
    public string? CentralResponse { get; set; }

    public int? CentralStatusCode { get; set; }

    public string? LastError { get; set; }
}
