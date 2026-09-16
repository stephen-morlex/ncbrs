namespace NCBRS.Models;

/// <summary>
/// Immutable record of every write/edit/reprint. Satisfies the legal
/// chain-of-custody requirement (Section 4.3 of the NCBRS draft). Never
/// updated or deleted -- only ever inserted.
/// </summary>
public class AuditLog
{
    public Guid AuditLogId { get; set; } = Guid.CreateVersion7();

    public required string EntityType { get; set; }
    public required string EntityId { get; set; }

    public required string Action { get; set; } // e.g. "Create", "Amend", "ReprintCertificate"

    public Guid? UserId { get; set; }
    public required string DeviceId { get; set; }

    /// <summary>
    /// The request that caused this write, matching RequestLog.TransactionId.
    /// Lets a domain change be traced back to the exact call that made it,
    /// and to every other write that call performed.
    /// </summary>
    public Guid? TransactionId { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
