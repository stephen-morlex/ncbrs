namespace NCBRS.Models;

/// <summary>
/// One row per API request, regardless of whether it wrote anything to the
/// domain -- broader than AuditLog, which only covers domain writes.
///
/// TransactionId is supplied by the caller via the X-Transaction-Id header
/// and echoed back in the response envelope, so a client can correlate a
/// request with the server's record of it even when the response never
/// arrived. The server mints one only when the caller omits it.
/// </summary>
public class RequestLog
{
    public Guid RequestLogId { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// Not unique: a client replaying the same transaction id is a fact
    /// worth recording (and the basis for idempotency detection later),
    /// not an error to suppress.
    /// </summary>
    public required Guid TransactionId { get; set; }

    /// <summary>Calling application or channel, e.g. "MobileApp", "Web".</summary>
    public string? ClientId { get; set; }

    /// <summary>True when the caller did not supply a transaction id.</summary>
    public bool TransactionIdGenerated { get; set; }

    public required string Method { get; set; }
    public required string Path { get; set; }

    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public DateTime StartedAtUtc { get; set; }
}
