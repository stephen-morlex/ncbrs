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

    /// <summary>
    /// The device's signature over <see cref="Payload"/>, exactly as the device
    /// sent it in the signature header, and forwarded with it unchanged.
    ///
    /// Without it the centre, which requires a signature by default, refuses
    /// every forwarded batch -- the node carried the bytes the signature
    /// covers but not the signature, so nothing it forwarded could prove
    /// which device it came from. The node never checks or produces one: it
    /// holds no device keys, and it does not need to understand a batch to
    /// carry it. Null when the device sent none.
    /// </summary>
    public string? DeviceSignature { get; set; }

    public ForwardedBatchStatus Status { get; set; } = ForwardedBatchStatus.Queued;

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ForwardedAtUtc { get; set; }

    public int Attempts { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    /// <summary>
    /// When this row may next be tried. Backoff so a node that spent the
    /// night unable to reach the centre is not hammering it at dawn.
    ///
    /// Also the claim on a batch while a forward is in flight: set past the
    /// attempt's timeout before the request goes out, so the poller does not
    /// see a batch the controller is already forwarding as due and send it a
    /// second time. If the node dies mid-forward the claim simply lapses.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// How many attempts in a row the centre was reachable but did not finish
    /// before the node gave up. Each one lengthens the next attempt's timeout.
    ///
    /// Counted apart from ordinary failures because the remedy is the opposite
    /// one. An unreachable centre needs the node to wait; a centre that is
    /// working but slow needs the node to wait *longer on the request*. A
    /// timeout cancels the centre's transaction, which rolls back the whole
    /// batch, so retrying with the same timeout repeats the same lost work
    /// forever while reporting it like an outage.
    /// </summary>
    public int ConsecutiveTimeouts { get; set; }

    /// <summary>
    /// The centre's response body, stored so a device that was told "queued"
    /// can come back later and find out what actually happened to each record
    /// -- which BRNs were confirmed, which were duplicates, which failed.
    /// </summary>
    public string? CentralResponse { get; set; }

    public int? CentralStatusCode { get; set; }

    public string? LastError { get; set; }
}
