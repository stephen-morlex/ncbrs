using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// Voiding a registration that should never have existed.
///
/// Held at ministry level rather than district, unlike amendments and
/// duplicate adjudication. Those decide how a legal identity reads, or which
/// of two records carries it; this withdraws it altogether. A real
/// deployment would most often reach here through a court order, which is why
/// <see cref="AnnulmentReason.CourtOrdered"/> must cite one.
/// </summary>
[ApiController]
[Route("api/BirthRecords/{brn}/annulment")]
[Authorize(Policy = NcbrsRoles.CanAnnulRegistrations)]
[Produces("application/json")]
public class AnnulmentsController(
    AnnulmentService annulments,
    CurrentRegistrarService currentRegistrar) : ControllerBase
{
    /// <summary>
    /// Voids the registration. The record and its BRN are kept forever — the
    /// number may already be printed on a certificate or quoted in a school
    /// register, and it has to keep resolving to an explanation.
    ///
    /// Any valid certificate is revoked and published to the revocation list,
    /// so a verifier checking the printed document will refuse it.
    /// </summary>
    [HttpPost(Name = "AnnulBirthRecord")]
    [ProducesResponseType(typeof(AnnulRecordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AnnulRecordResponse>> Annul(
        string brn,
        ApiRequest<AnnulRecordRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var result = await annulments.AnnulAsync(
            brn,
            envelope.Data,
            registrar,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        if (result.Succeeded)
        {
            return Ok(result.Response);
        }

        return result.Result switch
        {
            AnnulmentResult.BirthRecordNotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Birth record not found.", "brn", result.Detail!)),

            AnnulmentResult.AlreadyAnnulled => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Already annulled.", "brn", result.Detail!)),

            AnnulmentResult.AuthorityReferenceRequired => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Authority reference required.",
                "data.authorityReference", result.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted.", "brn", result.Detail ?? "Not permitted."))
        };
    }
}
