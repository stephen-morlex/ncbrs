using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// The operational signals a monitor alerts on: outbox backlog and WAL
/// archiving (plan §17 item 19, RUNBOOK "Services and health").
///
/// **Anonymous, on purpose, and only aggregates.** A monitoring system has to
/// reach it without a user's token, and it names no person: counts and
/// timestamps only, the same stance as the Consumer's <c>/health</c>. Every
/// other endpoint stays behind the fallback policy.
///
/// **Always 200 when the service answers**, with <c>status</c> saying "ok" or
/// "degraded". A 503 would let a load balancer take the API out of rotation
/// because *archiving* is failing, while registrations are working fine, which
/// would turn an invisible fault into an outage. Alert on <c>status</c>, not
/// on the status code.
/// </summary>
[ApiController]
[Route("health")]
[AllowAnonymous]
[Produces("application/json")]
public class HealthController(OperationalHealthService health) : ControllerBase
{
    [HttpGet(Name = "GetOperationalHealth")]
    [ProducesResponseType(typeof(OperationalHealth), StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationalHealth>> Get()
        => await health.CheckAsync(HttpContext.RequestAborted);
}
