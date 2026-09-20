using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;

namespace NCBRS.Controllers;

/// <summary>
/// The administrative geography — country, states, counties and the areas
/// beneath them — as reference data for dependent location pickers.
///
/// Read-only here, and open to any signed-in caller: it names no person and
/// carries nothing sensitive, and a registrar filling in a facility's location
/// needs the same lists a district officer does. Creating areas (a payam or
/// boma encountered for the first time) is a separate, write-gated concern and
/// is not part of this endpoint.
/// </summary>
[ApiController]
[Route("api/administrative-areas")]
[Authorize]
[Produces("application/json")]
public class AdministrativeAreasController(NcbrsDbContext db) : ControllerBase
{
    /// <summary>
    /// Areas, for building a State → County → Payam/Block → Boma/Quarter
    /// picker one level at a time. Pass <paramref name="parentId"/> to fetch
    /// the children of a selected area, or <paramref name="level"/> to fetch
    /// every area at a level (for example all states). With neither, the
    /// country root is returned. The two combine: a parent and a level fetch
    /// that parent's children of that level.
    /// </summary>
    [HttpGet(Name = "GetAdministrativeAreas")]
    [ProducesResponseType(typeof(IReadOnlyList<AdministrativeAreaResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<AdministrativeAreaResponse>>> List(
        [FromQuery] Guid? parentId = null,
        [FromQuery] AdministrativeLevel? level = null)
    {
        var query = db.AdministrativeAreas.AsNoTracking();

        if (parentId is { } parent)
        {
            query = query.Where(area => area.ParentId == parent);
        }
        else if (level is null)
        {
            // No parent and no level named: the top of the tree.
            query = query.Where(area => area.ParentId == null);
        }

        if (level is { } wanted)
        {
            query = query.Where(area => area.Level == wanted);
        }

        var areas = await query
            .OrderBy(area => area.Name)
            .Select(area => new AdministrativeAreaResponse(
                area.AdministrativeAreaId, area.Name, area.Level, area.Code, area.ParentId))
            .ToListAsync(HttpContext.RequestAborted);

        return areas;
    }
}
