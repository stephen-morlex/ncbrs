using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// Verification of births registered after the statutory window
/// (draft Sections 4.1, 5.3).
///
/// Late registration is the ordinary route for a large share of rural births,
/// so this queue is a working caseload rather than an exception list. What a
/// district registrar is deciding is whether the evidence supports the
/// claimed date of birth -- a date that decides school entry, the age of
/// majority, marriage eligibility and pension timing, with nobody
/// contemporaneous left to contradict it.
/// </summary>
[ApiController]
[Route("api/late-registrations")]
[Authorize(Policy = NcbrsRoles.CanApproveLateRegistrations)]
[Produces("application/json")]
public class LateRegistrationsController(
    LateRegistrationService lateRegistrations,
    CurrentRegistrarService currentRegistrar) : ControllerBase
{
    /// <summary>
    /// Late registrations awaiting verification, oldest first. Each one is a
    /// family without a certificate, so age is the thing worth seeing.
    /// </summary>
    [HttpGet("pending", Name = "GetPendingLateRegistrations")]
    [ProducesResponseType(typeof(Page<PendingLateRegistrationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Page<PendingLateRegistrationResponse>>> Pending(
        [FromQuery] Guid? facilityId = null,
        [FromQuery] int limit = PageRequest.DefaultLimit,
        [FromQuery] string? after = null)
        => Ok(await lateRegistrations.PendingAsync(
            facilityId, new PageRequest { Limit = limit, After = after }, HttpContext.RequestAborted));

    /// <summary>
    /// Records the verification decision. Approval is what releases the
    /// certificate; refusal leaves the registration standing but
    /// uncertifiable, and is kept so the same claim cannot simply be refiled
    /// as though it had never been seen.
    /// </summary>
    [HttpPost("{lateRegistrationId:guid}/review", Name = "ReviewLateRegistration")]
    [ProducesResponseType(typeof(ReviewLateRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReviewLateRegistrationResponse>> Review(
        Guid lateRegistrationId,
        ApiRequest<ReviewLateRegistrationRequest> envelope)
    {
        var reviewer = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (reviewer is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var result = await lateRegistrations.ReviewAsync(
            lateRegistrationId,
            envelope.Data.Approve,
            envelope.Data.Note,
            reviewer,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        if (result.Succeeded)
        {
            return Ok(result.Response);
        }

        return result.Result switch
        {
            LateRegistrationReviewResult.NotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Late registration not found.",
                "lateRegistrationId", result.Detail!)),

            LateRegistrationReviewResult.AlreadyReviewed => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Already reviewed.",
                "lateRegistrationId", result.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted.",
                "lateRegistrationId", result.Detail ?? "Not permitted."))
        };
    }
}
