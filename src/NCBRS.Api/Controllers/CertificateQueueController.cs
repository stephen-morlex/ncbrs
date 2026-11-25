using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// Births whose certificate has not reached the family (plan §17 item 32): to
/// issue, to hand over, or waiting on the registry. Facility staff work their
/// own facility's list; a district officer their county's; the Ministry any
/// (<see cref="WorkScope"/>).
/// </summary>
[ApiController]
[Route("api/certificate-queue")]
[Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
[Produces("application/json")]
public class CertificateQueueController(
    CertificateQueueService queue,
    CurrentRegistrarService currentRegistrar,
    WorkScope workScope) : ControllerBase
{
    /// <summary>
    /// Oldest first, so the family who has waited longest is first. Pass
    /// <paramref name="stage"/> for one stage only.
    /// </summary>
    [HttpGet(Name = "GetCertificateQueue")]
    [ProducesResponseType(typeof(Page<CertificateQueueItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Page<CertificateQueueItem>>> List(
        [FromQuery] Guid? facilityId = null,
        [FromQuery] CertificateQueueStage? stage = null,
        [FromQuery] int limit = PageRequest.DefaultLimit,
        [FromQuery] string? after = null)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var (refusal, facility, county) = await workScope.ResolveAsync(User, registrar, facilityId, HttpContext.RequestAborted);
        if (refusal is not null)
        {
            return refusal;
        }

        return Ok(await queue.ListAsync(
            facility, county, stage, new PageRequest { Limit = limit, After = after }, HttpContext.RequestAborted));
    }
}
