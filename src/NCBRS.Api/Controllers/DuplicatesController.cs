using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// The review queue for suspected cross-facility duplicate registrations
/// (draft Section 6.6).
///
/// Adjudicating one is a judgement about a citizen's legal identity, so it
/// is restricted to oversight roles rather than the facility staff who filed
/// the records -- the person who registered a birth should not be the one
/// ruling on whether it was a duplicate.
/// </summary>
[ApiController]
[Route("api/duplicates")]
[Authorize(Policy = NcbrsRoles.CanReviewDuplicates)]
[Produces("application/json")]
public class DuplicatesController(
    DuplicateDetectionService duplicates,
    CurrentRegistrarService currentRegistrar) : ControllerBase
{
    /// <summary>Potential duplicates awaiting a decision, most likely first.</summary>
    [HttpGet("pending")]
    [ProducesResponseType(typeof(Page<DuplicateCandidateResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Page<DuplicateCandidateResponse>>> Pending(
        [FromQuery] Guid? facilityId = null,
        [FromQuery] int limit = PageRequest.DefaultLimit,
        [FromQuery] string? after = null)
    {
        var pending = await duplicates.PendingAsync(
            facilityId, new PageRequest { Limit = limit, After = after }, HttpContext.RequestAborted);

        var items = pending.Items.Select(link => new DuplicateCandidateResponse(
                link.DuplicateCandidateId,
                link.Score,
                link.Reasons,
                link.BirthRecord?.Brn ?? string.Empty,
                link.BirthRecord?.ChildPerson?.FullName ?? string.Empty,
                link.MatchedBirthRecord?.Brn ?? string.Empty,
                link.MatchedBirthRecord?.ChildPerson?.FullName ?? string.Empty,
                link.DetectedAtUtc))
            .ToList();

        // The page's shape is decided by the service; this only maps entities
        // to their response type, so the cursor and total pass through
        // untouched.
        return new Page<DuplicateCandidateResponse>(items, pending.Total, pending.NextCursor);
    }

    /// <summary>
    /// Records a decision. Confirming marks the later registration as
    /// superseded by the earlier one; nothing is deleted.
    /// </summary>
    [HttpPost("{duplicateCandidateId:guid}/review")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Review(
        Guid duplicateCandidateId,
        ApiRequest<ReviewDuplicateRequest> envelope)
    {
        var reviewer = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (reviewer is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var result = await duplicates.ReviewAsync(
            duplicateCandidateId,
            envelope.Data.IsDuplicate,
            reviewer,
            envelope.Data.Note,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        return result.Result switch
        {
            DuplicateReviewResult.Reviewed => NoContent(),

            DuplicateReviewResult.NotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Duplicate candidate not found.",
                "duplicateCandidateId", result.Detail!)),

            DuplicateReviewResult.AlreadyReviewed => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Already reviewed.",
                "duplicateCandidateId", result.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted.",
                "duplicateCandidateId", result.Detail ?? "Not permitted."))
        };
    }
}
