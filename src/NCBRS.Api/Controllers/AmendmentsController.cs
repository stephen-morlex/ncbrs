using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// The approval queue for corrections that change how the register
/// identifies a person (draft Section 5.3).
///
/// Separate from the amend endpoint on BirthRecordsController because this is
/// a different job done by different people: a district officer works through
/// a queue across facilities, rather than correcting one record in front of
/// them.
/// </summary>
[ApiController]
[Route("api/amendments")]
[Authorize(Policy = NcbrsRoles.CanApproveAmendments)]
[Produces("application/json")]
public class AmendmentsController(
    AmendmentService amendments,
    CurrentRegistrarService currentRegistrar) : ControllerBase
{
    /// <summary>
    /// Corrections waiting on a reviewer, oldest first. A correction left
    /// sitting here is a family holding a certificate that the register
    /// already knows is wrong, so age is the thing worth seeing.
    /// </summary>
    [HttpGet("pending", Name = "GetPendingAmendments")]
    [ProducesResponseType(typeof(Page<PendingAmendmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Page<PendingAmendmentResponse>>> Pending(
        [FromQuery] Guid? facilityId = null,
        [FromQuery] int limit = PageRequest.DefaultLimit,
        [FromQuery] string? after = null)
        => Ok(await amendments.PendingAsync(
            facilityId, new PageRequest { Limit = limit, After = after }, HttpContext.RequestAborted));

    /// <summary>
    /// Corrections that arrived having been composed against a value the
    /// register no longer held (draft 6.3), oldest first.
    ///
    /// Separate from the approval queue, which asks whether a change should
    /// be made. This one says a change has already been resolved by
    /// last-writer-wins and the wrong value may have won — so age here
    /// measures how long a possibly-wrong value has been standing on a legal
    /// record.
    /// </summary>
    [HttpGet("conflicts", Name = "GetAmendmentConflicts")]
    [ProducesResponseType(typeof(Page<AmendmentConflictResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Page<AmendmentConflictResponse>>> Conflicts(
        [FromQuery] Guid? facilityId = null,
        [FromQuery] int limit = PageRequest.DefaultLimit,
        [FromQuery] string? after = null)
        => Ok(await amendments.ConflictsAsync(
            facilityId, new PageRequest { Limit = limit, After = after }, HttpContext.RequestAborted));

    /// <summary>
    /// Records a registrar's judgement on a flagged conflict: the resolution
    /// stands, or they have corrected it by a subsequent amendment.
    ///
    /// There is deliberately no way to change the value from here. Restoring
    /// what the register previously held is an ordinary amendment, and
    /// keeping it on that path means one piece of code stays responsible for
    /// previous values, approval rules and certificate withdrawal.
    /// </summary>
    [HttpPost("conflicts/{amendmentConflictId:guid}/review", Name = "ReviewAmendmentConflict")]
    [ProducesResponseType(typeof(ReviewAmendmentConflictResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReviewAmendmentConflictResponse>> ReviewConflict(
        Guid amendmentConflictId,
        ApiRequest<ReviewAmendmentConflictRequest> envelope)
    {
        var reviewer = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (reviewer is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var result = await amendments.ReviewConflictAsync(
            amendmentConflictId,
            envelope.Data.Uphold,
            envelope.Data.Note,
            reviewer,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        if (result.Succeeded)
        {
            return Ok(new ReviewAmendmentConflictResponse(
                amendmentConflictId,
                result.Response!.Brn,
                envelope.Data.Uphold ? AmendmentConflictStatus.Upheld : AmendmentConflictStatus.Corrected,
                result.Response.ReviewedAtUtc));
        }

        return result.Result switch
        {
            AmendmentReviewResult.NotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Conflict not found.",
                "amendmentConflictId", result.Detail!)),

            AmendmentReviewResult.AlreadyReviewed => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Already reviewed.",
                "amendmentConflictId", result.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted.",
                "amendmentConflictId", result.Detail ?? "Not permitted."))
        };
    }

    /// <summary>
    /// Approves or refuses a submitted correction.
    ///
    /// Approval is the moment the change takes effect and any certificate it
    /// contradicts is withdrawn. Until then the record reads exactly as it
    /// did, which is why nothing downstream was told about it.
    /// </summary>
    [HttpPost("{amendmentRequestId:guid}/review", Name = "ReviewAmendment")]
    [ProducesResponseType(typeof(ReviewAmendmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReviewAmendmentResponse>> Review(
        Guid amendmentRequestId,
        ApiRequest<ReviewAmendmentRequest> envelope)
    {
        var reviewer = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (reviewer is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var result = await amendments.ReviewAsync(
            amendmentRequestId,
            envelope.Data.Approve,
            reviewer,
            envelope.Data.Note,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        if (result.Succeeded)
        {
            return Ok(result.Response);
        }

        return result.Result switch
        {
            AmendmentReviewResult.NotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Amendment request not found.",
                "amendmentRequestId", result.Detail!)),

            AmendmentReviewResult.AlreadyReviewed => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Already reviewed.",
                "amendmentRequestId", result.Detail!)),

            // The record moved under the proposal. A conflict, not a refusal:
            // the correction may still be right, but not against these values.
            AmendmentReviewResult.Conflict => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Record changed since submission.",
                "amendmentRequestId", result.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted.",
                "amendmentRequestId", result.Detail ?? "Not permitted."))
        };
    }
}
