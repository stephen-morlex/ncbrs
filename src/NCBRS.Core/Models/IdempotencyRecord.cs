namespace NCBRS.Models;

public enum IdempotencyStatus
{
    /// <summary>Claimed and executing. A lease guards against the holder dying mid-request.</summary>
    InProgress,

    /// <summary>Finished successfully; the stored response is replayed on retry.</summary>
    Completed
}

/// <summary>
/// One row per client-supplied transaction id, guaranteeing a state-changing
/// request executes at most once no matter how many times a device retries.
///
/// This is deliberately separate from RequestLog. RequestLog is the audit of
/// what calls arrived -- three retries of one registration are three rows,
/// and that history is worth keeping. This table answers a different
/// question: has the work behind this key already been done, and what did we
/// answer last time?
///
/// Only successful (2xx) outcomes are recorded as Completed. A rejected or
/// failed attempt releases its key, so a device can fix its payload or retry
/// after an outage without being locked out of registering a birth.
/// </summary>
public class IdempotencyRecord
{
    public Guid IdempotencyRecordId { get; set; } = Guid.CreateVersion7();

    /// <summary>The caller's transaction id. Unique -- this is the whole point.</summary>
    public required Guid TransactionId { get; set; }

    public string? ClientId { get; set; }

    public IdempotencyStatus Status { get; set; } = IdempotencyStatus.InProgress;

    /// <summary>
    /// Hash of method, path and payload. A retry that reuses a key with a
    /// different body is a client bug -- in a civil registry that could mean
    /// one birth's id attached to another's data, so it's rejected loudly
    /// rather than silently replayed.
    /// </summary>
    public required string RequestFingerprint { get; set; }

    /// <summary>
    /// While InProgress, when this claim goes stale. A process that crashes
    /// mid-registration leaves its claim behind; once the lease lapses the
    /// next retry takes it over instead of being blocked forever.
    /// </summary>
    public DateTime LeaseExpiresAtUtc { get; set; }

    public int? ResponseStatusCode { get; set; }

    /// <summary>Serialized payload of the original response, replayed verbatim.</summary>
    public string? ResponseBody { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}
