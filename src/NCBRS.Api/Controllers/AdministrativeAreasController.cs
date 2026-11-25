using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// The administrative geography — country, states, counties and the areas
/// beneath them — as reference data for dependent location pickers.
///
/// Reading is open to any signed-in caller: it names no person and carries
/// nothing sensitive, and a registrar filling in a facility's location needs
/// the same lists a district officer does. Adding an area below a county is
/// the Ministry's, like adding a facility (<see cref="Create"/>).
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

    /// <summary>
    /// Adds a payam, block, boma, quarter or village inside a county: a place
    /// the official dataset does not hold, recorded as it is met so a facility
    /// can be placed exactly. The Ministry's act, like adding a facility. The
    /// registry gives the code; states and counties are never added here.
    /// </summary>
    [HttpPost(Name = "CreateAdministrativeArea")]
    [Authorize(Policy = NcbrsRoles.CanManageFacilities)]
    [ProducesResponseType(typeof(AdministrativeAreaResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AdministrativeAreaResponse>> Create(
        ApiRequest<CreateAdministrativeAreaRequest> envelope,
        [FromServices] CurrentRegistrarService currentRegistrar,
        [FromServices] AdministrativeAreaService areas)
    {
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);

        if (caller is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden,
                "No registrar record for this account.",
                string.Empty,
                "This account is not provisioned in the registry."));
        }

        var outcome = await areas.CreateAsync(
            envelope.Data, caller, TransactionContext.Get(HttpContext)?.TransactionId, HttpContext.RequestAborted);

        return outcome.Result switch
        {
            AreaCreationResult.Created => StatusCode(StatusCodes.Status201Created, new AdministrativeAreaResponse(
                outcome.Area!.AdministrativeAreaId, outcome.Area.Name, outcome.Area.Level, outcome.Area.Code, outcome.Area.ParentId)),

            AreaCreationResult.ParentNotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "No such area.", "data.parentId", outcome.Detail!)),

            AreaCreationResult.NameTaken => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Name already used here.", "data.name", outcome.Detail!)),

            AreaCreationResult.WrongLevel => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Not a level that can sit here.", "data.level", outcome.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Not inside a county.", "data.parentId", outcome.Detail!)),
        };
    }
}
