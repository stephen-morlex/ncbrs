using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

public enum DuplicateReviewResult
{
    Reviewed,
    NotFound,
    AlreadyReviewed,
    NotPermitted
}

public record DuplicateReviewOutcome(DuplicateReviewResult Result, string? Detail = null)
{
    public bool Succeeded => Result is DuplicateReviewResult.Reviewed;
}

/// <summary>
/// Finds and adjudicates cross-facility duplicate registrations
/// (draft Section 6.6).
///
/// Detection runs after a birth is already registered, never before: the
/// registration must succeed regardless, because refusing one on a
/// heuristic would leave a real child without a legal identity. What the
/// check produces is a queue for a human.
/// </summary>
public class DuplicateDetectionService(
    NcbrsDbContext db,
    DuplicateMatcher matcher,
    CertificateRevocationRecorder revocations,
    ILogger<DuplicateDetectionService> logger,
    DistrictLookup districts)
{
    /// <summary>
    /// Compares a newly registered birth against existing records and
    /// records any candidates. Returns how many were flagged.
    ///
    /// Never throws into the caller: a failure here must not undo a
    /// registration that is already valid and recorded.
    /// </summary>
    public async Task<int> ScanAsync(Guid birthRecordId, CancellationToken cancellationToken = default)
    {
        try
        {
            var candidate = await LoadAsync(birthRecordId, cancellationToken);
            if (candidate is null)
            {
                return 0;
            }

            var windowStart = candidate.DateOfBirth.Date.AddDays(-DuplicateMatcher.DateWindowDays);
            var windowEnd = candidate.DateOfBirth.Date.AddDays(DuplicateMatcher.DateWindowDays + 1);

            // Blocked on the date window so this stays a narrow indexed range
            // scan rather than a comparison against the whole registry.
            var others = await db.BirthRecords
                .Include(record => record.ChildPerson)
                .Include(record => record.MotherPerson)
                .Where(record => record.BirthRecordId != birthRecordId
                                 && record.SupersededByBirthRecordId == null
                                 && record.AnnulledAtUtc == null
                                 && record.DateOfBirth >= windowStart
                                 && record.DateOfBirth < windowEnd)
                .ToListAsync(cancellationToken);

            var flagged = 0;

            foreach (var other in others)
            {
                var assessment = matcher.Assess(candidate, other);

                if (assessment.Score < DuplicateMatcher.ReviewThreshold)
                {
                    continue;
                }

                var alreadyLinked = await db.DuplicateCandidates.AnyAsync(
                    link => (link.BirthRecordId == birthRecordId && link.MatchedBirthRecordId == other.BirthRecordId)
                            || (link.BirthRecordId == other.BirthRecordId && link.MatchedBirthRecordId == birthRecordId),
                    cancellationToken);

                if (alreadyLinked)
                {
                    continue;
                }

                db.DuplicateCandidates.Add(new DuplicateCandidate
                {
                    BirthRecordId = birthRecordId,
                    MatchedBirthRecordId = other.BirthRecordId,
                    Score = assessment.Score,
                    Reasons = string.Join(" ", assessment.Reasons)
                });

                flagged++;
            }

            if (flagged > 0)
            {
                await db.SaveChangesAsync(cancellationToken);

                logger.LogInformation(
                    "Flagged {Count} potential duplicate(s) for birth record {BirthRecordId}",
                    flagged, birthRecordId);
            }

            return flagged;
        }
        catch (Exception ex)
        {
            // Swallowed for the same reason a Kafka publish failure is: the
            // registration is the thing that legally matters, and it has
            // already happened.
            logger.LogError(ex, "Duplicate scan failed for birth record {BirthRecordId}", birthRecordId);
            return 0;
        }
    }

    public async Task<Page<DuplicateCandidate>> PendingAsync(
        Guid? facilityId,
        PageRequest paging,
        CancellationToken cancellationToken = default)
    {
        var query = db.DuplicateCandidates
            .Include(link => link.BirthRecord!).ThenInclude(record => record.ChildPerson)
            .Include(link => link.MatchedBirthRecord!).ThenInclude(record => record.ChildPerson)
            .Where(link => link.Status == DuplicateReviewStatus.Pending);

        if (facilityId is not null)
        {
            query = query.Where(link =>
                link.BirthRecord!.FacilityId == facilityId
                || link.MatchedBirthRecord!.FacilityId == facilityId);
        }

        var total = await query.CountAsync(cancellationToken);

        // Ordered by match score, not by time: the strongest candidates are the
        // ones a reviewer should see first. The cursor therefore walks scores
        // downward, and the id breaks ties between identical scores -- without
        // it two candidates scoring the same could straddle a page boundary and
        // one would never be reviewed.
        if (PageCursor.TryDecode(paging.After, out var cursor) && cursor.IsInt())
        {
            var score = cursor.AsInt();

            query = query.Where(link =>
                link.Score < score
                || (link.Score == score && link.DuplicateCandidateId.CompareTo(cursor.Id) > 0));
        }

        var rows = await query
            .OrderByDescending(link => link.Score)
            .ThenBy(link => link.DuplicateCandidateId)
            .Take(paging.EffectiveLimit + 1)
            .ToListAsync(cancellationToken);

        return Page<DuplicateCandidate>.From(rows, total, paging.EffectiveLimit,
            last => PageCursor.For(last.Score, last.DuplicateCandidateId));
    }

    /// <summary>
    /// Records a reviewer's judgement.
    ///
    /// Confirming marks the newer record superseded by the older one rather
    /// than deleting anything: the registry is append-only for legal
    /// reasons, and a certificate already issued against the superseded BRN
    /// has to remain explicable rather than referring to a record that
    /// vanished.
    /// </summary>
    public async Task<DuplicateReviewOutcome> ReviewAsync(
        Guid duplicateCandidateId,
        bool isDuplicate,
        Registrar reviewer,
        string? note,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var link = await db.DuplicateCandidates
            .Include(candidate => candidate.BirthRecord)
            .Include(candidate => candidate.MatchedBirthRecord)
            .FirstOrDefaultAsync(candidate => candidate.DuplicateCandidateId == duplicateCandidateId, cancellationToken);

        if (link is null)
        {
            return new DuplicateReviewOutcome(DuplicateReviewResult.NotFound,
                $"No duplicate candidate exists with id '{duplicateCandidateId}'.");
        }

        if (link.Status != DuplicateReviewStatus.Pending)
        {
            return new DuplicateReviewOutcome(DuplicateReviewResult.AlreadyReviewed,
                $"This candidate was already {link.Status.ToString().ToLowerInvariant()}.");
        }

        link.Status = isDuplicate ? DuplicateReviewStatus.Confirmed : DuplicateReviewStatus.Dismissed;
        link.ReviewedByRegistrarId = reviewer.RegistrarId;
        link.ReviewedAtUtc = DateTime.UtcNow;
        link.ReviewNote = note;

        if (isDuplicate)
        {
            // The earlier registration survives: it is the one closest to the
            // birth, and the one a family is most likely already holding a
            // certificate for.
            var (surviving, superseded) = Order(link.BirthRecord!, link.MatchedBirthRecord!);

            superseded.SupersededByBirthRecordId = surviving.BirthRecordId;

            // A certificate issued against the losing registration is now a
            // document for an identity the register no longer recognises.
            // Leaving it valid would be the sharper failure of the two this
            // list guards against: an amendment leaves a stale certificate,
            // but this leaves a whole second legal identity standing.
            var certificate = await db.Certificates.FirstOrDefaultAsync(
                entry => entry.BirthRecordId == superseded.BirthRecordId && entry.WithdrawnAtUtc == null,
                cancellationToken);

            if (certificate is not null)
            {
                revocations.Revoke(
                    certificate,
                    RevocationReason.SupersededAsDuplicate,
                    link.ReviewedAtUtc!.Value,
                    reviewer.RegistrarId,
                    transactionId,
                    detail: $"Superseded as a duplicate of BRN '{surviving.Brn}'.");
            }

            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(BirthRecord),
                EntityId = superseded.Brn,
                DistrictId = await districts.ForBrnAsync(superseded.Brn, cancellationToken),
                Action = "SupersededAsDuplicate",
                UserId = reviewer.RegistrarId,
                DeviceId = "review",
                TransactionId = transactionId
            });
        }

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(DuplicateCandidate),
            EntityId = duplicateCandidateId.ToString(),
            // The record under review, which exists whichever way the
            // decision goes -- `superseded` only exists on the confirm path.
            DistrictId = await districts.ForRecordAsync(link.BirthRecord!, cancellationToken),
            Action = isDuplicate ? "ConfirmDuplicate" : "DismissDuplicate",
            UserId = reviewer.RegistrarId,
            DeviceId = "review",
            TransactionId = transactionId
        });

        await db.SaveChangesAsync(cancellationToken);

        return new DuplicateReviewOutcome(DuplicateReviewResult.Reviewed);
    }

    private static (BirthRecord Surviving, BirthRecord Superseded) Order(BirthRecord left, BirthRecord right)
        => left.CreatedAtUtc <= right.CreatedAtUtc ? (left, right) : (right, left);

    private Task<BirthRecord?> LoadAsync(Guid birthRecordId, CancellationToken cancellationToken)
        => db.BirthRecords
            .Include(record => record.ChildPerson)
            .Include(record => record.MotherPerson)
            .FirstOrDefaultAsync(record => record.BirthRecordId == birthRecordId, cancellationToken);
}
