using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// The offline device PIN (draft Section 6.7).
///
/// Keycloak secures the server, but a village post is disconnected for weeks
/// and cannot reach a token endpoint. Staff still need to prove who they are
/// to the tablet in their hands, so devices cache PIN hashes for their
/// facility and verify them locally.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
[Produces("application/json")]
public class DeviceCredentialsController(
    NcbrsDbContext db,
    DevicePinHasher hasher,
    CurrentRegistrarService currentRegistrar,
    CountyLookup districts) : ControllerBase
{
    /// <summary>
    /// Sets or replaces the caller's own offline PIN.
    ///
    /// Note there is deliberately no endpoint to verify a PIN against the
    /// server. Verification happens on the device, and exposing it here
    /// would hand an attacker an oracle to brute-force a six-digit secret
    /// over the network.
    /// </summary>
    [HttpPut("me/device-pin", Name = "SetOwnDevicePin")]
    [ProducesResponseType(typeof(SetDevicePinResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SetDevicePinResponse>> SetPin(ApiRequest<SetDevicePinRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var policy = hasher.CheckPolicy(envelope.Data.Pin);
        if (!policy.Acceptable)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "PIN rejected.", "data.pin", policy.Reason!));
        }

        // Replacing an existing PIN requires the old one. A token alone
        // should not be enough to lock a colleague out of the device they
        // share, and tokens outlive the moment they were issued.
        if (registrar.CredentialHash is not null
            && !hasher.Verify(envelope.Data.CurrentPin ?? string.Empty, registrar.CredentialHash))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Current PIN required.",
                "data.currentPin", "The current PIN must be supplied correctly to change it."));
        }

        var isFirstPin = registrar.CredentialHash is null;
        registrar.CredentialHash = hasher.Hash(envelope.Data.Pin);

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Registrar),
            CountyCode = await districts.ForRegistrarAsync(registrar, HttpContext.RequestAborted),
            EntityId = registrar.RegistrarId.ToString(),
            Action = isFirstPin ? "SetDevicePin" : "ChangeDevicePin",
            UserId = registrar.RegistrarId,
            DeviceId = "self-service",
            TransactionId = TransactionContext.Get(HttpContext)?.TransactionId
        });

        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return new SetDevicePinResponse(registrar.RegistrarId, registrar.DisplayName, DateTime.UtcNow);
    }

    /// <summary>
    /// The credential bundle a device caches to authenticate staff offline.
    ///
    /// This hands out PIN hashes, which is an unavoidable consequence of
    /// offline verification: the device has to hold something to check
    /// against. The exposure is narrowed as far as it can be -- the caller
    /// must belong to the facility, only that facility's registrars are
    /// returned, registrars with no PIN are omitted, and every fetch is
    /// audited so a bundle pulled by a compromised account is visible after
    /// the fact.
    /// </summary>
    [HttpGet("facilities/{facilityId:guid}/device-credentials", Name = "GetFacilityDeviceCredentials")]
    [Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
    [ProducesResponseType(typeof(DeviceCredentialBundle), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceCredentialBundle>> GetCredentials(
        Guid facilityId,
        [FromQuery] string? deviceId = null)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        if (!currentRegistrar.CanActForFacility(registrar, facilityId))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "facilityId", $"You are not permitted to provision devices for facility '{facilityId}'."));
        }

        var facility = await db.Facilities.FindAsync([facilityId], HttpContext.RequestAborted);
        if (facility is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Facility not found.",
                "facilityId", $"No facility exists with id '{facilityId}'."));
        }

        var credentials = await db.Registrars
            .Where(person => person.FacilityId == facilityId && person.CredentialHash != null)
            .OrderBy(person => person.DisplayName)
            .Select(person => new DeviceCredentialEntry(
                person.RegistrarId,
                person.DisplayName,
                person.Role,
                person.CredentialHash!))
            .ToListAsync(HttpContext.RequestAborted);

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Facility),
            CountyCode = await districts.ForFacilityAsync(facilityId, HttpContext.RequestAborted),
            EntityId = facilityId.ToString(),
            Action = "IssueDeviceCredentials",
            UserId = registrar.RegistrarId,
            DeviceId = deviceId ?? "unspecified",
            TransactionId = TransactionContext.Get(HttpContext)?.TransactionId
        });

        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return new DeviceCredentialBundle(
            facilityId,
            facility.Name,
            DateTime.UtcNow,
            credentials);
    }

    private static ObjectResult NotProvisioned()
        => ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status403Forbidden, "Account not provisioned.",
            "registrar", "This account is not linked to a registrar in the registry."));
}
