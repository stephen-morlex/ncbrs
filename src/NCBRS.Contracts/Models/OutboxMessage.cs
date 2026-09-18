namespace NCBRS.Models;

/// <summary>
/// An event staged for Kafka, written in the same transaction as the domain
/// change that produced it.
///
/// This is what makes the two consistent. Publishing directly from a request
/// meant the event went out before the transaction committed: a rollback
/// afterwards left consumers told about a registration that never landed,
/// and a crash between commit and publish lost the event entirely. Writing
/// the row transactionally means the event exists exactly when the change
/// does, and a relay delivers it afterwards.
/// </summary>
public class OutboxMessage
{
    public Guid OutboxMessageId { get; set; } = Guid.CreateVersion7();

    public required string Topic { get; set; }

    /// <summary>
    /// Kafka message key. Carries the district so a district's events land on
    /// one partition and stay ordered relative to each other -- the
    /// partitioning the draft calls for (Section 6.4.1), which a null key
    /// could never provide.
    /// </summary>
    public string? PartitionKey { get; set; }

    /// <summary>The serialized event, exactly as it will be produced.</summary>
    public required string Payload { get; set; }

    /// <summary>Kept for tracing an event back to the call that caused it.</summary>
    public Guid? TransactionId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null until the broker has acknowledged the message.</summary>
    public DateTime? DispatchedAtUtc { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Why the last attempt failed, for diagnosing a stuck outbox.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Lease held by a relay instance while it attempts delivery, so several
    /// API instances do not each publish the same message.
    /// </summary>
    public DateTime? LockedUntilUtc { get; set; }
}
