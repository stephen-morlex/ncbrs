namespace NCBRS.Models;

/// <summary>
/// Where a proposed correction stands.
///
/// Two tracks (draft 5.3): a correction to a field the certificate signature
/// does not cover takes effect at once, because a misspelled birth weight is
/// clerical and holding it behind a district officer helps nobody. A change
/// to a signed field -- the child's name, date of birth or sex -- is a change
/// to how the register identifies a person, so it waits for someone other
/// than its author.
/// </summary>
public enum AmendmentStatus
{
    /// <summary>In effect on the record.</summary>
    Applied,

    /// <summary>Proposed, awaiting a reviewer who is not its author.</summary>
    PendingApproval,

    /// <summary>Refused by a reviewer. Kept: a refused change is history too.</summary>
    Rejected
}

/// <summary>
/// One field corrected on a registered birth, with what it said before.
///
/// Stored per field rather than as a snapshot so "what was changed, and from
/// what" is answerable directly -- the question anyone auditing a disputed
/// record actually asks. The previous value is the point: a civil register
/// corrects by adding to the history, never by overwriting it, so the
/// original entry remains provable years later.
///
/// The BRN is deliberately not amendable. It is the permanent identifier the
/// whole offline block-allocation design exists to keep stable, and it may
/// already be printed on a certificate in a family's hands.
/// </summary>
public class BirthRecordAmendment
{
    public Guid BirthRecordAmendmentId { get; set; } = Guid.CreateVersion7();

    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    /// <summary>The amended property, named as the API exposes it.</summary>
    public required string Field { get; set; }

    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>
    /// Why the correction was made. Required, because an unexplained change
    /// to a legal record is indistinguishable from tampering after the fact.
    /// </summary>
    public required string Reason { get; set; }

    public Guid AmendedByRegistrarId { get; set; }
    public Registrar? AmendedByRegistrar { get; set; }

    /// <summary>When the correction was submitted, not when it took effect.</summary>
    public DateTime AmendedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? TransactionId { get; set; }

    /// <summary>
    /// Groups the fields submitted together, so a reviewer approves the
    /// correction a registrar actually made rather than adjudicating its
    /// fields one at a time and half-applying someone's intent.
    /// </summary>
    public Guid AmendmentRequestId { get; set; }

    public AmendmentStatus Status { get; set; } = AmendmentStatus.Applied;

    /// <summary>
    /// When this change took effect. Equal to <see cref="AmendedAtUtc"/> on
    /// the immediate track; the approval moment on the other; null while
    /// pending or if rejected.
    /// </summary>
    public DateTime? AppliedAtUtc { get; set; }

    public Guid? ReviewedByRegistrarId { get; set; }
    public Registrar? ReviewedByRegistrar { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }

    public string? ReviewNote { get; set; }
}
