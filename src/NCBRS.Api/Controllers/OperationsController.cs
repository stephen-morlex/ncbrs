using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// How each facility is doing, for the people keeping a pilot running (plan §17
/// item 35): refused births, numbers still provisional, review queues and their
/// age, quiet devices and certificates not yet collected. Facility staff see
/// their own facility; a district officer their county; the Ministry the
/// country (<see cref="WorkScope"/>).
/// </summary>
[ApiController]
[Route("api/operations")]
[Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
[Produces("application/json")]
public class OperationsController(
    FacilityOperationsService operations,
    CurrentRegistrarService currentRegistrar,
    WorkScope workScope) : ControllerBase
{
    /// <summary>One row per facility in scope, ordered by county and name.</summary>
    [HttpGet("facilities", Name = "GetFacilityOperations")]
    [ProducesResponseType(typeof(FacilityOperationsReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<FacilityOperationsReport>> Facilities([FromQuery] Guid? facilityId = null)
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

        return Ok(await operations.ReportAsync(facility, county, HttpContext.RequestAborted));
    }
}
