using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// Problems reported from tablets, with the tablet's own diagnostics (plan §17
/// item 37). A tablet sends; the district officers who support it read and
/// resolve, within their own county, as with device alerts.
/// </summary>
[ApiController]
[Route("api/problem-reports")]
[Produces("application/json")]
public class ProblemReportsController(
    ProblemReportService reports,
    CurrentRegistrarService currentRegistrar,
    CountyScopeResolver scopes,
    DeviceChannelGate channelGate) : ControllerBase
{
    /// <summary>
    /// A report from a tablet. Held to the device-channel rule like every
    /// write that names a device: the trail says which tablet reported, and
    /// only the tablet can prove it. Sending the same report again is answered
    /// with the one already stored.
    /// </summary>
    [HttpPost(Name = "ReportProblem")]
    [Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
    [ProducesResponseType(typeof(ProblemReportResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ProblemReportResponse>> Report(ApiRequest<ReportProblemRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var request = envelope.Data;

        if (!await currentRegistrar.CanActForFacilityAsync(registrar, request.FacilityId, HttpContext.RequestAborted))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.", "data.facilityId",
                "This account does not act for that facility."));
        }

        var refused = await channelGate.RefuseUnlessPermittedAsync(
            HttpContext, registrar, request.DeviceId, request.FacilityId,
            nameof(DeviceProblemReport), request.ReportId.ToString());
        if (refused is not null)
        {
            return refused;
        }

        var outcome = await reports.StoreAsync(
            request, registrar, TransactionContext.Get(HttpContext)?.TransactionId, HttpContext.RequestAborted);

        return outcome.Result switch
        {
            ProblemReportResult.Stored => StatusCode(StatusCodes.Status201Created, outcome.Report),
            ProblemReportResult.AlreadyStored => outcome.Report!,
            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted.", "data.reportId", outcome.Detail!)),
        };
    }

    /// <summary>
    /// Reports from the caller's county, newest first; the Ministry's from
    /// everywhere. Unresolved only unless asked.
    /// </summary>
    [HttpGet(Name = "GetProblemReports")]
    [Authorize(Policy = NcbrsRoles.CanEnrolDevices)]
    [ProducesResponseType(typeof(IReadOnlyList<ProblemReportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ProblemReportResponse>>> List([FromQuery] bool includeResolved = false)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var scope = await scopes.ResolveAsync(User, registrar, requestedCountyCode: null, HttpContext.RequestAborted);
        if (!scope.IsAllowed)
        {
            return ApiErrors.Result(ApiErrors.Single(StatusCodes.Status403Forbidden, scope.Title, scope.Field, scope.Message));
        }

        return Ok(await reports.ListAsync(scope.Scope.CountyCode, includeResolved, HttpContext.RequestAborted));
    }

    /// <summary>Records that a report was dealt with, and how.</summary>
    [HttpPost("{reportId:guid}/resolve", Name = "ResolveProblemReport")]
    [Authorize(Policy = NcbrsRoles.CanEnrolDevices)]
    [ProducesResponseType(typeof(ProblemReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProblemReportResponse>> Resolve(Guid reportId, ApiRequest<ResolveProblemReportRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var outcome = await reports.ResolveAsync(
            reportId, envelope.Data.Note, registrar,
            facility => currentRegistrar.CanActForFacilityAsync(registrar, facility, HttpContext.RequestAborted).AsTask(),
            TransactionContext.Get(HttpContext)?.TransactionId, HttpContext.RequestAborted);

        return outcome.Result switch
        {
            ProblemReportResult.Stored => outcome.Report!,
            ProblemReportResult.NotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Report not found.", "reportId", outcome.Detail!)),
            ProblemReportResult.AlreadyResolved => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Already resolved.", "reportId", outcome.Detail!)),
            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.", "reportId", outcome.Detail!)),
        };
    }

    private static ObjectResult NotProvisioned()
        => ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status403Forbidden, "Account not provisioned.",
            "registrar", "This account is not linked to a registrar in the registry."));
}
