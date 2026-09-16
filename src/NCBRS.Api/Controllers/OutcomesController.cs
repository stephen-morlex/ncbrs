using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// Records what happened after a birth event.
///
/// These are sub-resources of a BirthRecord rather than edits to it: per
/// WHO/UN standards a live birth and a subsequent death are two separate
/// legal records, never one row with an "outcome" column (CLAUDE.md design
/// decision #3). Registering the birth and recording the death are
/// therefore distinct acts, each with its own audit entry and author.
/// </summary>
[ApiController]
[Route("api/BirthRecords/{brn}")]
[Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
[Produces("application/json")]
public class OutcomesController(
    OutcomeService outcomes,
    CurrentRegistrarService currentRegistrar) : ControllerBase
{
    /// <summary>
    /// Records a death within 28 days of a live birth, classified per WHO
    /// ICD-PM.
    /// </summary>
    [HttpPost("neonatal-outcome", Name = "RecordNeonatalOutcome")]
    [ProducesResponseType(typeof(NeonatalOutcomeResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<NeonatalOutcomeResponse>> RecordNeonatal(
        string brn,
        ApiRequest<RecordNeonatalOutcomeRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var result = await outcomes.RecordNeonatalAsync(
            brn,
            envelope.Data,
            registrar,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        return result.Succeeded
            ? StatusCode(StatusCodes.Status201Created, result.Response)
            : MapFailure(result.Result, result.Detail);
    }

    /// <summary>
    /// Records a maternal death linked to this birth event, classified per
    /// WHO ICD-MM.
    /// </summary>
    [HttpPost("maternal-outcome", Name = "RecordMaternalOutcome")]
    [ProducesResponseType(typeof(MaternalOutcomeResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MaternalOutcomeResponse>> RecordMaternal(
        string brn,
        ApiRequest<RecordMaternalOutcomeRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var result = await outcomes.RecordMaternalAsync(
            brn,
            envelope.Data,
            registrar,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        return result.Succeeded
            ? StatusCode(StatusCodes.Status201Created, result.Response)
            : MapFailure(result.Result, result.Detail);
    }

    private static ObjectResult NotProvisioned()
        => ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status403Forbidden, "Account not provisioned.",
            "registrar", "This account is not linked to a registrar in the registry."));

    private static ObjectResult MapFailure(OutcomeResult result, string? detail)
        => result switch
        {
            OutcomeResult.BirthRecordNotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Birth record not found.", "brn", detail!)),

            OutcomeResult.AlreadyRecorded => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Outcome already recorded.", "brn", detail!)),

            OutcomeResult.NotPermitted => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.", "brn", detail!)),

            // A state conflict rather than a bad request: nothing about the
            // payload is wrong, the record it names has been voided.
            OutcomeResult.RecordAnnulled => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Registration annulled.", "brn", detail!)),

            // A WHO definition was broken -- a 400 naming the rule, so the
            // caller can tell a typo from a genuinely different vital event.
            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Outcome rejected.", "data", detail!))
        };
}
