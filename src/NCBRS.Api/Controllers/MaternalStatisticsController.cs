using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// The statistical questionnaire alongside a registration (draft 6.5.1;
/// UN P&amp;R Rev. 3).
///
/// Legally a distinct function from the registration, which is why it has its
/// own endpoint and its own lifecycle: it can be completed later, revised
/// freely, or never filled in at all, and none of that touches the legal
/// record or the certificate.
/// </summary>
[ApiController]
[Route("api/BirthRecords/{brn}/maternal-statistics")]
[Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
[Produces("application/json")]
public class MaternalStatisticsController(
    MaternalStatisticsService statistics,
    CurrentRegistrarService currentRegistrar) : ControllerBase
{
    /// <summary>
    /// Records or revises the questionnaire.
    ///
    /// PUT rather than POST because it is one questionnaire per birth and
    /// re-submitting replaces it: a statistics clerk completing what a
    /// midwife left blank is doing the same act, not a second one. No
    /// approval workflow — a corrected statistic is simply a better
    /// statistic, unlike a correction to the legal record.
    /// </summary>
    [HttpPut(Name = "SaveMaternalStatistics")]
    [ProducesResponseType(typeof(MaternalStatisticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MaternalStatisticsResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MaternalStatisticsResponse>> Put(
        string brn,
        ApiRequest<CaptureMaternalStatisticsRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var result = await statistics.CaptureAsync(
            brn,
            envelope.Data,
            registrar,
            envelope.Data.DeviceId,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        if (result.Succeeded)
        {
            return result.Revised
                ? Ok(result.Response)
                : StatusCode(StatusCodes.Status201Created, result.Response);
        }

        return result.Result switch
        {
            MaternalStatisticsResult.BirthRecordNotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Birth record not found.", "brn", result.Detail!)),

            MaternalStatisticsResult.RecordAnnulled => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Registration annulled.", "brn", result.Detail!)),

            // The payload is well-formed; its answers disagree with each
            // other or with the record, which is a 400 naming the clash.
            MaternalStatisticsResult.Inconsistent => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Inconsistent answers.", "data", result.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "brn", result.Detail ?? "Not permitted."))
        };
    }

    [HttpGet(Name = "GetMaternalStatistics")]
    [ProducesResponseType(typeof(MaternalStatisticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MaternalStatisticsResponse>> Get(string brn)
    {
        var found = await statistics.GetAsync(brn, HttpContext.RequestAborted);

        return found is null
            ? ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Statistics not recorded.",
                "brn", $"No statistical questionnaire has been recorded for BRN '{brn}'."))
            : found;
    }
}
