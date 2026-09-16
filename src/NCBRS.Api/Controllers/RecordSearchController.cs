using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// W1. Searching the register by name and date.
///
/// Separate from <c>BirthRecordsController</c> because it is a different act
/// under different rules. That controller answers "show me the record with
/// this number", which a family holding a certificate is entitled to ask.
/// This one answers "which records match this name", which is a surveillance
/// capability and is scoped, restricted and audited accordingly. Putting them
/// side by side would invite the second to inherit the first's openness.
/// </summary>
[ApiController]
[Route("api/birthrecords/search")]
[Authorize]
// Matches every other controller. Without it ApiExplorer also advertises
// text/plain and text/json, which this endpoint never returns -- and the
// generated client picks a content type off that list, so the document
// misdescribing the wire format reaches the browser as a wrong type.
[Produces("application/json")]
public class RecordSearchController(
    NcbrsDbContext db,
    RecordSearchService search,
    CurrentRegistrarService currentRegistrar,
    DistrictScopeResolver scopes) : ControllerBase
{
    /// <summary>
    /// Searches within the caller's district, or nationally for the Ministry.
    /// </summary>
    [HttpGet(Name = "SearchBirthRecords")]
    [ProducesResponseType(typeof(Page<BirthRecordSearchHit>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Page<BirthRecordSearchHit>>> Search(
        [FromQuery] string? name = null,
        [FromQuery] DateTime? bornFrom = null,
        [FromQuery] DateTime? bornTo = null,
        [FromQuery] Guid? facilityId = null,
        [FromQuery] RecordStatus? status = null,
        [FromQuery] string? districtId = null,
        [FromQuery] int limit = PageRequest.DefaultLimit,
        [FromQuery] string? after = null)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);

        // An authenticated subject with no registrar record was never
        // provisioned in the registry. They may hold a realm role, but there
        // is nobody here for the audit trail to name, and an unattributable
        // search is exactly the thing this endpoint must not permit.
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden,
                "No registrar record for this account.",
                string.Empty,
                "This account is not provisioned in the registry, so a search cannot be attributed to anyone."));
        }

        var criteria = new RecordSearchCriteria
        {
            Name = name,
            BornFrom = bornFrom,
            BornTo = bornTo,
            FacilityId = facilityId,
            Status = status
        };

        // Refused rather than answered with everything. A blank form must not
        // be a way to read a district out of the register, and a facility or
        // status alone is still "every birth at this clinic".
        if (!criteria.IsSpecific)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest,
                "A search needs a name or a date range.",
                "name",
                $"Supply a name of at least {RecordSearchService.MinimumNameLength} characters, "
                + "or a date-of-birth range. Searching a whole district is not a search."));
        }

        if (bornFrom is { } from && bornTo is { } to && to < from)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest,
                "The date range ends before it starts.",
                "bornTo",
                "'bornTo' must not be earlier than 'bornFrom'."));
        }

        var scope = await scopes.ResolveAsync(
            User, registrar, districtId, HttpContext.RequestAborted);

        if (!scope.IsAllowed)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, scope.Title, scope.Field, scope.Message));
        }

        var page = new PageRequest { Limit = limit, After = after };

        var results = await search.SearchAsync(
            criteria,
            scope.Scope,
            page,
            new AuditContext(
                registrar.RegistrarId,
                // Not a device: this is a person at a browser. Named rather
                // than blank so the trail says how the register was reached.
                "web",
                TransactionContext.Get(HttpContext)?.TransactionId),
            HttpContext.RequestAborted);

        // The audit row is staged by the service and committed here, in the
        // same save. A search that returned results without recording itself
        // would be the one failure mode this endpoint cannot have.
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return results;
    }

}
