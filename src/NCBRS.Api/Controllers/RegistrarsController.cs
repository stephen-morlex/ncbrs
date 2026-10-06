using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// W3. Who the people acting on the register are.
///
/// Queues and histories carry a `registrarId`, and several already carry the
/// name beside it. This is what resolves the ones that do not, and what
/// answers "who is provisioned here" — a question a district officer has to
/// be able to ask before they can tell whether an account should still exist.
///
/// **No `AuditLog` row.** That table covers domain writes, and reading a
/// colleague's name is not one; `RequestLog` already records one row per
/// `/api` request, so the call is not invisible. Record search is the
/// deliberate exception, because searching *citizens* by name is a
/// surveillance act rather than a read.
/// </summary>
[ApiController]
[Route("api/registrars")]
[Authorize]
[Produces("application/json")]
public class RegistrarsController(
    NcbrsDbContext db,
    CurrentRegistrarService currentRegistrar,
    CountyScopeResolver scopes) : ControllerBase
{
    /// <summary>
    /// The registrars in the caller's district, or nationally for the
    /// Ministry.
    ///
    /// Listing is an oversight act — knowing who holds an account is how a
    /// district notices one that should have been withdrawn — so it needs a
    /// role that oversees more than one facility. Resolving a single id does
    /// not; see <see cref="Get"/>.
    /// </summary>
    [HttpGet(Name = "GetRegistrars")]
    [Authorize(Policy = NcbrsRoles.CanEnrolDevices)]
    [ProducesResponseType(typeof(Page<RegistrarResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Page<RegistrarResponse>>> List(
        [FromQuery] string? name = null,
        [FromQuery] Guid? facilityId = null,
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

            query = query.Where(registrar =>
                EF.Functions.Like(registrar.DisplayName, $"%{fragment}%"));
        }

        if (facilityId is { } facility)
        {
            query = query.Where(registrar => registrar.FacilityId == facility);
        }

        var total = await query.CountAsync(HttpContext.RequestAborted);

        var page = new PageRequest { Limit = limit, After = after };

        // Ordered by name, which is how a person reads a directory. The id
        // breaks ties so the cursor is stable across two registrars who
        // share a display name -- which is likelier here than it sounds,
        // since these are typed by hand at provisioning.
        var ordered = query
            .OrderBy(registrar => registrar.DisplayName)
            .ThenBy(registrar => registrar.RegistrarId);

        if (PageCursor.TryDecode(page.After, out var cursor))
        {
            var afterName = cursor.SortKey;

            ordered = (IOrderedQueryable<Registrar>)ordered.Where(registrar =>
                string.Compare(registrar.DisplayName, afterName) > 0
                || (registrar.DisplayName == afterName && registrar.RegistrarId > cursor.Id));
        }

        var fetched = await ordered
            .Include(registrar => registrar.Facility)
            .Take(page.EffectiveLimit + 1)
            .ToListAsync(HttpContext.RequestAborted);

        var result = Page<Registrar>.From(
            fetched,
            total,
            page.EffectiveLimit,
            registrar => new PageCursor(registrar.DisplayName, registrar.RegistrarId));

        return new Page<RegistrarResponse>(
            result.Items.Select(ToResponse).ToList(),
            result.Total,
            result.NextCursor);
    }

    /// <summary>
    /// One registrar, by id.
    ///
    /// Open to any provisioned caller within the same district, because this
    /// is what turns a `registrarId` in a queue into a person. A facility
    /// registrar reading "0199a1b2-…" approved my correction cannot audit
    /// anything; the same row naming a district officer can be questioned.
    ///
    /// Still district-scoped. Where a response legitimately names someone
    /// outside the caller's district, it should carry the name itself rather
    /// than expect this to resolve it — several already do.
    /// </summary>
    [HttpGet("{registrarId:guid}", Name = "GetRegistrar")]
    [ProducesResponseType(typeof(RegistrarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegistrarResponse>> Get(Guid registrarId)
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

        var registrar = await InScope(scope.Scope)
            .Include(entry => entry.Facility)
            .FirstOrDefaultAsync(entry => entry.RegistrarId == registrarId, HttpContext.RequestAborted);

        // 404 rather than 403 for someone outside the caller's district.
        // Distinguishing "no such registrar" from "a registrar you may not
        // see" would confirm that a given id exists elsewhere, which is the
        // thing the scope is there to withhold.
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound,
                "No such registrar.",
                "registrarId",
                "No registrar with that id in your district."));
        }

        return ToResponse(registrar);
    }

    /// <summary>
    /// Who the signed-in account is to the registry. Not gated on being
    /// provisioned: this is how an account that is not yet declares itself.
    /// It is recorded as pending, from what its own token says, so a district
    /// officer can find it and bind it (pilot readiness §1).
    /// </summary>
    [HttpGet("/api/me", Name = "GetMe")]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MeResponse>> Me([FromServices] RegistrarOnboardingService onboarding)
    {
        var status = await onboarding.RecordAsync(User, HttpContext.RequestAborted);

        return new MeResponse(
            Provisioned: status.Registrar is not null && !status.Withdrawn,
            Withdrawn: status.Withdrawn,
            Pending: status.Pending is not null,
            Registrar: status.Registrar is { } registrar ? ToResponse(registrar) : null);
    }

    /// <summary>
    /// The accounts waiting to be bound: those in the officer's county group,
    /// or every one for the Ministry. An account with no county group waits
    /// for the Ministry.
    /// </summary>
    [HttpGet("pending", Name = "GetPendingAccounts")]
    [Authorize(Policy = NcbrsRoles.CanManageRegistrars)]
    [ProducesResponseType(typeof(IReadOnlyList<PendingAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PendingAccountResponse>>> Pending(
        [FromServices] RegistrarOnboardingService onboarding)
    {
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (caller is null)
        {
            return NotProvisioned();
        }

        var accounts = await onboarding.PendingForAsync(caller, User, HttpContext.RequestAborted);
        if (accounts is null)
        {
            return ApiErrors.Result(ApiErrors.Single(StatusCodes.Status403Forbidden,
                "Your county is not known.", string.Empty,
                "Your facility cannot be placed in a county, so which accounts are yours cannot be decided."));
        }

        return accounts.Select(account => new PendingAccountResponse(
            account.PendingAccountId, account.DisplayName, account.Username, account.Email,
            [.. account.RealmRoles.Split(',', StringSplitOptions.RemoveEmptyEntries)],
            account.CountyCode, account.FirstSeenAtUtc, account.LastSeenAtUtc)).ToList();
    }

    /// <summary>Bind a waiting account to a facility and a role (pilot readiness §1).</summary>
    [HttpPost(Name = "BindRegistrar")]
    [Authorize(Policy = NcbrsRoles.CanManageRegistrars)]
    [ProducesResponseType(typeof(RegistrarResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegistrarResponse>> Bind(
        ApiRequest<BindRegistrarRequest> envelope, [FromServices] RegistrarOnboardingService onboarding)
    {
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (caller is null)
        {
            return NotProvisioned();
        }

        var outcome = await onboarding.BindAsync(
            envelope.Data, caller, User, TransactionContext.Get(HttpContext)?.TransactionId, HttpContext.RequestAborted);

        return outcome.Result == RegistrarOnboardingResult.Done
            ? CreatedAtAction(nameof(Get), new { registrarId = outcome.Registrar!.RegistrarId }, ToResponse(outcome.Registrar))
            : Failure(outcome);
    }

    /// <summary>
    /// Withdraw a registrar who has stopped working here. The row stays (the
    /// trail names them); they resolve as unprovisioned from now on, and their
    /// PIN leaves each tablet at its next sync.
    /// </summary>
    [HttpPost("{registrarId:guid}/withdraw", Name = "WithdrawRegistrar")]
    [Authorize(Policy = NcbrsRoles.CanManageRegistrars)]
    [ProducesResponseType(typeof(RegistrarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegistrarResponse>> Withdraw(
        Guid registrarId, ApiRequest<WithdrawRegistrarRequest> envelope, [FromServices] RegistrarOnboardingService onboarding)
    {
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (caller is null)
        {
            return NotProvisioned();
        }

        var outcome = await onboarding.WithdrawAsync(
            registrarId, envelope.Data, caller, User, TransactionContext.Get(HttpContext)?.TransactionId, HttpContext.RequestAborted);

        return outcome.Result == RegistrarOnboardingResult.Done ? ToResponse(outcome.Registrar!) : Failure(outcome);
    }

    /// <summary>
    /// Clear the offline PIN of a registrar who has forgotten it. The officer
    /// never sets the new one: the registrar does, signed in with their own
    /// account, and the old PIN stops unlocking each tablet at that tablet's
    /// next sync. Within the officer's county; facility staff only for a
    /// district officer; never one's own.
    /// </summary>
    [HttpPost("{registrarId:guid}/reset-device-pin", Name = "ResetRegistrarDevicePin")]
    [Authorize(Policy = NcbrsRoles.CanManageRegistrars)]
    [ProducesResponseType(typeof(RegistrarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegistrarResponse>> ResetDevicePin(
        Guid registrarId, [FromServices] RegistrarOnboardingService onboarding)
    {
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (caller is null)
        {
            return NotProvisioned();
        }

        var outcome = await onboarding.ResetPinAsync(
            registrarId, caller, User, TransactionContext.Get(HttpContext)?.TransactionId, HttpContext.RequestAborted);

        return outcome.Result == RegistrarOnboardingResult.Done ? ToResponse(outcome.Registrar!) : Failure(outcome);
    }

    private static ObjectResult Failure(RegistrarOnboardingOutcome outcome)
        => ApiErrors.Result(ApiErrors.Single(
            outcome.Result switch
            {
                RegistrarOnboardingResult.NotFound => StatusCodes.Status404NotFound,
                RegistrarOnboardingResult.NotPermitted => StatusCodes.Status403Forbidden,
                RegistrarOnboardingResult.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            },
            outcome.Result switch
            {
                RegistrarOnboardingResult.NotFound => "Not found.",
                RegistrarOnboardingResult.NotPermitted => "Not permitted.",
                RegistrarOnboardingResult.Conflict => "Already done.",
                _ => "Not accepted.",
            },
            outcome.Field ?? string.Empty,
            outcome.Detail ?? string.Empty));

    private IQueryable<Registrar> InScope(SearchScope scope)
    {
        var query = db.Registrars.AsNoTracking();

        return scope.CountyCode is { } districtId
            ? query.Where(registrar =>
                registrar.Facility != null && registrar.Facility.CountyCode == districtId)
            : query;
    }

    private ActionResult NotProvisioned() => ApiErrors.Result(ApiErrors.Single(
        StatusCodes.Status403Forbidden,
        "No registrar record for this account.",
        string.Empty,
        "This account is not provisioned in the registry."));

    /// <summary>
    /// What a directory entry may show.
    ///
    /// Not <c>CredentialHash</c>, which is the offline PIN and has one
    /// legitimate destination — the credential bundle a device caches — and
    /// no business in a directory. Only whether there is one, which is what an
    /// officer needs to know before resetting it. Not <c>ExternalSubjectId</c> either: that
    /// is the Keycloak subject, an identifier for the identity provider
    /// rather than for anyone here, and publishing it invites callers to key
    /// their own records on it.
    /// </summary>
    private static RegistrarResponse ToResponse(Registrar registrar) => new(
        registrar.RegistrarId,
        registrar.DisplayName,
        registrar.Role,
        registrar.FacilityId,
        registrar.Facility?.Name ?? string.Empty,
        registrar.Facility?.CountyCode ?? string.Empty,
        registrar.WithdrawnAtUtc,
        registrar.CredentialHash is not null);
}

public record RegistrarResponse(
    Guid RegistrarId,
    string DisplayName,
    RegistrarRole Role,
    Guid FacilityId,
    string FacilityName,
    string CountyCode,

    /// <summary>
    /// When they stopped working here, if they have: they stay listed, because
    /// the trail names them. The reason is not published. Anyone may resolve a
    /// colleague by id, and why someone left is an HR matter.
    /// </summary>
    DateTime? WithdrawnAtUtc = null,

    /// <summary>Whether they have an offline PIN set: whether there is anything to reset.</summary>
    bool HasDevicePin = false);

public record MeResponse(bool Provisioned, bool Withdrawn, bool Pending, RegistrarResponse? Registrar);

public record PendingAccountResponse(
    Guid PendingAccountId,
    string DisplayName,
    string? Username,
    string? Email,
    IReadOnlyList<string> RealmRoles,
    string? CountyCode,
    DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc);
