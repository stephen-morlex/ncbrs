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
    /// The district this act belongs to, so the trail can be read by the
    /// district it concerns and not only by the people who happened to
    /// perform it.
    ///
    /// **`required` rather than nullable, deliberately.** Twenty-five places
    /// write to this table, and a nullable column would let any one of them
    /// omit the district silently — reintroducing, one call site at a time,
    /// exactly the gap this closes. Making the compiler refuse is the only
    /// enforcement that cannot be forgotten.
    ///
    /// <see cref="Unattributed"/> is the honest answer where an act belongs
    /// to no district — a national export, a Ministry-wide read — and
    /// <see cref="Unknown"/> is what rows written before this column existed
    /// carry. They are different facts and must not be conflated: the first
    /// is a decision, the second is an absence.
    ///
    /// Rows written before the migration keep <see cref="Unknown"/> forever.
    /// `AuditLogs` is append-only and enforced so at the database, so they
    /// can never be given a district retroactively — which is precisely why
    /// this column had to be added before the table grew rather than after.
    /// </summary>
    public required string DistrictId { get; set; }

    /// <summary>Written before this column existed. Never assigned by code.</summary>
    public const string Unknown = "";

    /// <summary>An act that genuinely belongs to no single district.</summary>
    public const string Unattributed = "national";

    /// <summary>
    /// The request that caused this write, matching RequestLog.TransactionId.
    /// Lets a domain change be traced back to the exact call that made it,
    /// and to every other write that call performed.
    /// </summary>
    public Guid? TransactionId { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
