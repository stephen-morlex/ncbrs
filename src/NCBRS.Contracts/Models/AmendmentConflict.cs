namespace NCBRS.Models;

public enum AmendmentConflictStatus
{
    /// <summary>Detected and waiting for a registrar to look at it.</summary>
    PendingReview,

    /// <summary>A registrar has seen it and let the resolution stand.</summary>
    Upheld,

    /// <summary>
    /// A registrar has seen it and corrected the outcome by a subsequent
    /// amendment. The conflict row stays either way.
    /// </summary>
    Corrected
}

/// <summary>
/// One field where an amendment arrived having been composed against a value
/// the register no longer held (draft 6.3).
///
/// The case is a device that spent weeks offline: a health worker corrects a
/// birth weight on the tablet, while a district officer corrects the same
/// field centrally. Both are acting in good faith on the information in front
/// of them, and neither knows about the other.
///
/// The draft's rule is last-writer-wins with an audit trail, flagged for
/// registrar review rather than auto-merged silently. This row is that flag.
/// It exists because the resolution is mechanical and the judgement is not:
/// the machine can pick a winner deterministically, but only a person can
/// tell whether the right value won, and without a record nobody would ever
/// learn the other value had existed.
/// </summary>
public class AmendmentConflict
{
    public Guid AmendmentConflictId { get; set; } = Guid.CreateVersion7();

    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    /// <summary>The submission that arrived stale.</summary>
    public Guid AmendmentRequestId { get; set; }

    public required string Field { get; set; }

    /// <summary>What the device believed the record said when it composed the change.</summary>
    public string? ExpectedPreviousValue { get; set; }

    /// <summary>What the register actually held by the time it arrived.</summary>
    public string? ActualPreviousValue { get; set; }

    /// <summary>
    /// The value that won. Null while the incoming change is still waiting on
    /// approval -- a conflict on an identity field is flagged at submission,
    /// but nothing is applied until a reviewer approves it.
    /// </summary>
    public string? ResolvedValue { get; set; }

    public AmendmentConflictStatus Status { get; set; } = AmendmentConflictStatus.PendingReview;

    public Guid DetectedFromRegistrarId { get; set; }

    public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? ReviewedByRegistrarId { get; set; }
    public Registrar? ReviewedByRegistrar { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }

    public string? ReviewNote { get; set; }

    public Guid? TransactionId { get; set; }
}
