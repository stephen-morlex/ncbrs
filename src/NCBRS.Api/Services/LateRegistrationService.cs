using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

public enum LateRegistrationReviewResult
{
    Reviewed,
    NotFound,
    AlreadyReviewed,
    NotPermitted
}

public record LateRegistrationReviewOutcome(
    LateRegistrationReviewResult Result,
    ReviewLateRegistrationResponse? Response = null,
    string? Detail = null)
{
    public bool Succeeded => Result is LateRegistrationReviewResult.Reviewed;
}

/// <summary>
/// Verification of births registered after the statutory window
/// (draft Sections 4.1, 5.3).
///
/// The registration itself always succeeds -- a child registered late is
/// still a child who exists, and refusing the record outright would leave
/// them with no legal identity at all, which is the opposite of what this
/// system is for. What waits on verification is the certificate.
/// </summary>
public class LateRegistrationService(
    NcbrsDbContext db,
    CurrentRegistrarService currentRegistrar)
{
    /// <summary>The queue a district registrar works from, oldest first.</summary>
    public async Task<IReadOnlyList<PendingLateRegistrationResponse>> PendingAsync(
        Guid? facilityId,
        CancellationToken cancellationToken = default)
    {
        var query = db.LateRegistrations
            .AsNoTracking()
            .Include(late => late.BirthRecord!).ThenInclude(record => record.ChildPerson)
            .Include(late => late.BirthRecord!).ThenInclude(record => record.Facility)
            .Include(late => late.SubmittedByRegistrar)
            .Where(late => late.Status == LateRegistrationStatus.PendingApproval);

        if (facilityId is not null)
        {
            query = query.Where(late => late.BirthRecord!.FacilityId == facilityId);
        }

        return await query
            .OrderBy(late => late.SubmittedAtUtc)
            .Select(late => new PendingLateRegistrationResponse(
                late.LateRegistrationId,
                late.BirthRecord!.Brn,
                late.BirthRecord.ChildPerson!.FullName,
                late.BirthRecord.DateOfBirth,
                late.DaysLate,
                late.WindowDaysAtFiling,
                late.EvidenceType,
                late.EvidenceReference,
                late.DeclarantName,
                late.DeclarantRelationship,
                late.BirthRecord.FacilityId,
                late.BirthRecord.Facility!.Name,
                late.SubmittedByRegistrarId,
                late.SubmittedByRegistrar!.DisplayName,
                late.SubmittedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<LateRegistrationReviewOutcome> ReviewAsync(
        Guid lateRegistrationId,
        bool approve,
        string note,
        Registrar reviewer,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var late = await db.LateRegistrations
            .Include(entry => entry.BirthRecord)
            .FirstOrDefaultAsync(entry => entry.LateRegistrationId == lateRegistrationId, cancellationToken);

        if (late is null || late.BirthRecord is null)
        {
            return new LateRegistrationReviewOutcome(LateRegistrationReviewResult.NotFound,
                Detail: $"No late registration exists with id '{lateRegistrationId}'.");
        }

        if (late.Status != LateRegistrationStatus.PendingApproval)
        {
            return new LateRegistrationReviewOutcome(LateRegistrationReviewResult.AlreadyReviewed,
                Detail: $"This late registration was already {late.Status.ToString().ToLowerInvariant()}.");
        }

        if (!currentRegistrar.CanActForFacility(reviewer, late.BirthRecord.FacilityId))
        {
            return new LateRegistrationReviewOutcome(LateRegistrationReviewResult.NotPermitted,
                Detail: "You are not permitted to verify late registrations for this facility.");
        }

        // Separation of duties, as for amendments: the registrar who filed
        // the claim is not the one who confirms the evidence behind it.
        if (late.SubmittedByRegistrarId == reviewer.RegistrarId)
        {
            return new LateRegistrationReviewOutcome(LateRegistrationReviewResult.NotPermitted,
                Detail: "A late registration cannot be verified by the registrar who filed it.");
        }

        var reviewedAt = DateTime.UtcNow;

        late.Status = approve ? LateRegistrationStatus.Approved : LateRegistrationStatus.Rejected;
        late.ReviewedByRegistrarId = reviewer.RegistrarId;
        late.ReviewedAtUtc = reviewedAt;
        late.ReviewNote = note;

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(LateRegistration),
            EntityId = late.BirthRecord.Brn,
            Action = approve ? "ApproveLateRegistration" : "RejectLateRegistration",
            UserId = reviewer.RegistrarId,
            DeviceId = "review",
            TransactionId = transactionId
        });

        await db.SaveChangesAsync(cancellationToken);

        return new LateRegistrationReviewOutcome(
            LateRegistrationReviewResult.Reviewed,
            new ReviewLateRegistrationResponse(
                late.LateRegistrationId, late.BirthRecord.Brn, late.Status, reviewedAt, note));
    }
}
