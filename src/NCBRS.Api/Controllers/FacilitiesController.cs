using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// W2. The facilities a district is responsible for, and how close each is to
/// running out of registration numbers.
///
/// Two endpoints already took a `facilityId`, but nothing could produce one or
/// say anything about a facility — so BRN block state was visible only in the
/// database, and the first sign of exhaustion was a family leaving with a
/// provisional slip.
///
/// **Block state is the reason this exists.** A list of facility names would
/// be a directory; what a district officer needs is which of their posts will
/// stop issuing real numbers, in time to do something about it.
/// </summary>
[ApiController]
[Route("api/facilities")]
[Authorize]
[Produces("application/json")]
public class FacilitiesController(
    NcbrsDbContext db,
    CurrentRegistrarService currentRegistrar,
    CountyScopeResolver scopes,
    BrnBlockOptions blockOptions) : ControllerBase
{
    /// <summary>
    /// The facilities in the caller's district, or nationally for the
    /// Ministry.
    ///
    /// Open to any provisioned caller rather than gated on an oversight role.
    /// A facility registrar needs to know their own post is running low —
    /// they are the one who will be handing out slips — and the scope already
    /// confines them to their district.
    /// </summary>
    [HttpGet(Name = "GetFacilities")]
    [ProducesResponseType(typeof(Page<FacilityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Page<FacilityResponse>>> List(
        [FromQuery] string? name = null,
        [FromQuery] FacilityTier? tier = null,
        [FromQuery] BrnBlockStatus? blockStatus = null,
        [FromQuery] string? districtId = null,
        [FromQuery] int limit = PageRequest.DefaultLimit,
        [FromQuery] string? after = null)
    {
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);

        if (caller is null)
        {
            return NotProvisioned();
        }

        var scope = await scopes.ResolveAsync(
            User, caller, districtId, HttpContext.RequestAborted);

        if (!scope.IsAllowed)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, scope.Title, scope.Field, scope.Message));
        }

        var query = InScope(scope.Scope);

        if (!string.IsNullOrWhiteSpace(name))
        {
            var fragment = name.Trim();

            query = query.Where(facility => EF.Functions.Like(facility.Name, $"%{fragment}%"));
        }

        if (tier is { } wanted)
        {
            query = query.Where(facility => facility.Tier == wanted);
        }

        var total = await query.CountAsync(HttpContext.RequestAborted);

        var page = new PageRequest { Limit = limit, After = after };

        var ordered = query
            .OrderBy(facility => facility.Name)
            .ThenBy(facility => facility.FacilityId);

        if (PageCursor.TryDecode(page.After, out var cursor))
        {
            var afterName = cursor.SortKey;

            ordered = (IOrderedQueryable<Facility>)ordered.Where(facility =>
                string.Compare(facility.Name, afterName) > 0
                || (facility.Name == afterName && facility.FacilityId > cursor.Id));
        }

        var fetched = await ordered
            .Take(page.EffectiveLimit + 1)
            .ToListAsync(HttpContext.RequestAborted);

        var result = Page<Facility>.From(
            fetched,
            total,
            page.EffectiveLimit,
            facility => new PageCursor(facility.Name, facility.FacilityId));

        var items = result.Items.Select(ToResponse).ToList();

        // Filtered here rather than in SQL because the threshold depends on
        // the facility's connectivity profile, which makes it a comparison
        // between two columns and a configured number per row -- expressible,
        // but at the cost of putting the rule in two places. The page is
        // bounded, so the honest trade is a filter that can return fewer rows
        // than the limit rather than a duplicated threshold that can drift.
        if (blockStatus is { } status)
        {
            items = items.Where(facility => facility.BlockStatus == status).ToList();
        }

        return new Page<FacilityResponse>(items, result.Total, result.NextCursor);
    }

    /// <summary>One facility, by id.</summary>
    [HttpGet("{facilityId:guid}", Name = "GetFacility")]
    [ProducesResponseType(typeof(FacilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FacilityResponse>> Get(Guid facilityId)
    {
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);

        if (caller is null)
        {
            return NotProvisioned();
        }

        var scope = await scopes.ResolveAsync(
            User, caller, requestedCountyCode: null, HttpContext.RequestAborted);

        if (!scope.IsAllowed)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, scope.Title, scope.Field, scope.Message));
        }

        var facility = await InScope(scope.Scope)
            .FirstOrDefaultAsync(entry => entry.FacilityId == facilityId, HttpContext.RequestAborted);

        // 404 rather than 403 for a facility outside the caller's district,
        // matching the registrar directory: distinguishing "no such facility"
        // from "one you may not see" would confirm the id exists elsewhere.
        if (facility is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound,
                "No such facility.",
                "facilityId",
                "No facility with that id in your district."));
        }

        return ToResponse(facility);
    }

    private IQueryable<Facility> InScope(SearchScope scope)
    {
        var query = db.Facilities.AsNoTracking();

        return scope.CountyCode is { } districtId
            ? query.Where(facility => facility.CountyCode == districtId)
            : query;
    }

    private ActionResult NotProvisioned() => ApiErrors.Result(ApiErrors.Single(
        StatusCodes.Status403Forbidden,
        "No registrar record for this account.",
        string.Empty,
        "This account is not provisioned in the registry."));

    /// <summary>
    /// The block boundaries are published alongside the derived status, not
    /// instead of it. A district officer deciding whether to grant a block
    /// wants the status; someone reconciling a disputed BRN wants the actual
    /// range the facility was given, and that is a question only the numbers
    /// answer.
    /// </summary>
    private FacilityResponse ToResponse(Facility facility) => new(
        facility.FacilityId,
        facility.Name,
        facility.Tier,
        facility.CountyCode,
        facility.ConnectivityProfile,
        facility.BrnBlockStart,
        facility.BrnBlockEnd,
        facility.BrnBlockNextAvailable,
        BrnBlockHealth.Remaining(facility),
        BrnBlockHealth.StatusOf(facility, blockOptions),
        blockOptions.WarnBelow(facility.ConnectivityProfile));
}

public record FacilityResponse(
    Guid FacilityId,
    string Name,
    FacilityTier Tier,
    string CountyCode,
    ConnectivityProfile ConnectivityProfile,
    long BrnBlockStart,
    long BrnBlockEnd,
    long BrnBlockNextAvailable,

    /// <summary>Numbers still available to hand to a device.</summary>
    long BrnRemaining,

    BrnBlockStatus BlockStatus,

    /// <summary>
    /// The threshold this facility is judged against, published so a reader
    /// can see *why* a post with 400 numbers left is flagged while a hospital
    /// with 60 is not. A status without its threshold looks arbitrary.
    /// </summary>
    int BrnWarnBelow);
