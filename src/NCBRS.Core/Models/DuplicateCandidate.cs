namespace NCBRS.Models;

public enum DuplicateReviewStatus
{
    /// <summary>Detected by the matcher, awaiting a human decision.</summary>
    Pending,

    /// <summary>A reviewer judged these to be the same birth.</summary>
    Confirmed,

    /// <summary>A reviewer judged these to be different births.</summary>
    Dismissed
}

/// <summary>
/// A suspected cross-facility duplicate: the same birth registered twice,
/// typically because a mother delivered at a village post and was later
/// registered again at a hospital, each issuing a BRN from its own block.
/// Exact-BRN checks cannot catch this by construction -- the numbers are
/// different on purpose.
///
/// This is a flag for a human, never an automatic rejection. Matching on
/// names and dates produces false positives with certainty, and the cost of
/// a wrong automatic call is a real child left without a legal identity.
/// </summary>
public class DuplicateCandidate
{
    public Guid DuplicateCandidateId { get; set; } = Guid.CreateVersion7();

    /// <summary>The newly registered record that triggered the check.</summary>
    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    /// <summary>The existing record it resembles.</summary>
    public Guid MatchedBirthRecordId { get; set; }
    public BirthRecord? MatchedBirthRecord { get; set; }

    /// <summary>0-100. The threshold for recording a candidate is configurable.</summary>
    public int Score { get; set; }

    /// <summary>
    /// Why the matcher flagged this, in words. A reviewer deciding whether
    /// two records are one child needs to see what actually lined up, not
    /// just a number.
    /// </summary>
    public required string Reasons { get; set; }

    public DuplicateReviewStatus Status { get; set; } = DuplicateReviewStatus.Pending;

    public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? ReviewedByRegistrarId { get; set; }
    public Registrar? ReviewedByRegistrar { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }

    /// <summary>What the reviewer said, kept because this is a legal judgement.</summary>
    public string? ReviewNote { get; set; }
}

/// <summary>What a reviewer sends when adjudicating a suspected duplicate.</summary>
public record ReviewDuplicateRequest
{
    /// <summary>True when the two records are the same birth.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>Why. Kept because this is a legal judgement about an identity.</summary>
    public string? Note { get; init; }
}

public record DuplicateCandidateResponse(
    Guid DuplicateCandidateId,
    int Score,
    string Reasons,
    string Brn,
    string ChildFullName,
    string MatchedBrn,
    string MatchedChildFullName,
    DateTime DetectedAtUtc
);
