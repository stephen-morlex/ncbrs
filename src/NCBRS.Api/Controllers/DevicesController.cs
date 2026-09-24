using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// Device enrolment (WS-B9, draft 6.7).
///
/// Enrolment is a district act, not a self-service one. A device that could
/// enrol itself would close no hole at all -- whoever could reach the
/// endpoint would simply enrol whatever they were holding.
/// </summary>
[ApiController]
[Route("api/devices")]
[Authorize(Policy = NcbrsRoles.CanEnrolDevices)]
[Produces("application/json")]
public class DevicesController(
    NcbrsDbContext db,
    CurrentRegistrarService currentRegistrar,
    CountyLookup districts,
    CountyScopeResolver scopes) : ControllerBase
{
    /// <summary>
    /// The district's queue of devices that have gone quiet (plan F4).
    ///
    /// Open first, then acknowledged, longest silence at the top — the order
    /// an officer deciding where to drive on Thursday actually needs.
    /// Resolved alerts are excluded by default but kept: which posts keep
    /// going dark is the signal behind replacing hardware rather than
    /// rebooting it.
    /// </summary>
    [HttpGet("alerts", Name = "GetDeviceAlerts")]
    [ProducesResponseType(typeof(IReadOnlyList<DeviceAlertResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<DeviceAlertResponse>>> Alerts(
        [FromQuery] string? districtId = null,
        [FromQuery] bool includeResolved = false)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned<IReadOnlyList<DeviceAlertResponse>>();
        }

        // The same boundary as search: a district officer sees their own
        // county's queue (narrowed to it when none is named) and is refused,
        // not silently narrowed, when they name another. Only the Ministry
        // sees every county. Before this the queue was whatever the caller
        // asked for, and asking for nothing returned the whole country.
        var scope = await scopes.ResolveAsync(User, registrar, districtId, HttpContext.RequestAborted);
        if (!scope.IsAllowed)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, scope.Title, scope.Field, scope.Message));
        }

        var query = db.DeviceAlerts.AsQueryable();

        if (!includeResolved)
        {
            query = query.Where(alert => alert.ResolvedAtUtc == null);
        }

        if (scope.Scope.CountyCode is { } county)
        {
            query = query.Where(alert => alert.CountyCode == county);
        }

        var alerts = await query
            .OrderBy(alert => alert.Status)
            .ThenByDescending(alert => alert.DaysSilentWhenRaised)
            .ToListAsync(HttpContext.RequestAborted);

        return await AlertResponsesAsync(alerts, HttpContext.RequestAborted);
    }

    /// <summary>
    /// Records that someone is acting on an alert.
    ///
    /// This does **not** close it. Acknowledging says "I know, I am driving
    /// out there on Thursday"; only the device reporting again says the
    /// problem is over. If an acknowledgement resolved the alert, a district
    /// could empty its queue without a single device coming back — which is
    /// exactly the reporting gap this feature exists to surface.
    /// </summary>
    [HttpPost("alerts/{deviceAlertId:guid}/acknowledge", Name = "AcknowledgeDeviceAlert")]
    [ProducesResponseType(typeof(DeviceAlertResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceAlertResponse>> AcknowledgeAlert(
        Guid deviceAlertId,
        ApiRequest<AcknowledgeDeviceAlertRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned<DeviceAlertResponse>();
        }

        var alert = await db.DeviceAlerts
            .FirstOrDefaultAsync(entry => entry.DeviceAlertId == deviceAlertId, HttpContext.RequestAborted);

        if (alert is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Alert not found.",
                "deviceAlertId", $"No device alert exists with id '{deviceAlertId}'."));
        }

        // Acknowledging says "someone here is acting on this". From an officer
        // in another county that is false, and it is the harmful kind of false:
        // the alert's own district sees it handled and stops looking, which is
        // exactly how a queue empties without a single device coming back.
        if (!await currentRegistrar.CanActForFacilityAsync(registrar, alert.FacilityId, HttpContext.RequestAborted))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "deviceAlertId", "This alert belongs to a facility outside your county."));
        }

        if (alert.ResolvedAtUtc is not null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Alert already resolved.",
                "deviceAlertId",
                $"Device '{alert.DeviceId}' reported again on {alert.ResolvedAtUtc:u}; there is nothing to act on."));
        }

        alert.Status = DeviceAlertStatus.Acknowledged;
        alert.AcknowledgedAtUtc = DateTime.UtcNow;
        alert.AcknowledgedByRegistrarId = registrar.RegistrarId;
        alert.AcknowledgementNote = envelope.Data.Note;

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(DeviceAlert),
            CountyCode = alert.CountyCode,
            EntityId = alert.DeviceId,
            Action = "AcknowledgeDeviceAlert",
            UserId = registrar.RegistrarId,
            DeviceId = alert.DeviceId,
            TransactionId = TransactionContext.Get(HttpContext)?.TransactionId
        });

        await db.SaveChangesAsync(HttpContext.RequestAborted);

        // The acknowledger is the caller, so no lookup is needed -- and the
        // response naming them confirms to the person who just pressed the
        // button that the undertaking is recorded against them.
        return AlertResponse(alert, registrar.DisplayName);
    }

    /// <summary>Enrols a device against a facility.</summary>
    [HttpPost(Name = "EnrolDevice")]
    [ProducesResponseType(typeof(DeviceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceResponse>> Enrol(ApiRequest<EnrolDeviceRequest> envelope)
    {
        var request = envelope.Data;

        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Device id required.",
                "data.deviceId", "A device id is required."));
        }

        var facility = await db.Facilities.FindAsync([request.FacilityId], HttpContext.RequestAborted);
        if (facility is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Facility not found.",
                "data.facilityId", $"No facility exists with id '{request.FacilityId}'."));
        }

        if (!await currentRegistrar.CanActForFacilityAsync(registrar, facility.FacilityId, HttpContext.RequestAborted))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "data.facilityId", $"You are not permitted to enrol devices for facility '{request.FacilityId}'."));
        }

        var key = DeviceSignature.ValidateEnrolmentKey(request.PublicKeyPem);
        if (!key.Valid)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Public key rejected.",
                "data.publicKeyPem", key.Reason!));
        }

        var existing = await db.Devices
            .FirstOrDefaultAsync(device => device.DeviceId == request.DeviceId, HttpContext.RequestAborted);

        if (existing is not null)
        {
            // Deliberately refused rather than treated as a key update. A
            // re-enrolment that silently replaces the key is exactly the
            // move an attacker needs: it takes over an identity that every
            // record in the register is already attributed to. Replacing a
            // device means revoking the old enrolment first, which leaves
            // both acts in the audit trail.
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Device already enrolled.",
                "data.deviceId",
                $"Device '{request.DeviceId}' is already enrolled ({existing.Status}). "
                + "Revoke the existing enrolment before enrolling a replacement key."));
        }

        var device = new Device
        {
            DeviceId = request.DeviceId,
            FacilityId = request.FacilityId,
            PublicKeyPem = request.PublicKeyPem,
            Label = request.Label,
            EnrolledByRegistrarId = registrar.RegistrarId
        };

        db.Devices.Add(device);

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Device),
            CountyCode = await districts.ForFacilityAsync(device.FacilityId, HttpContext.RequestAborted),
            EntityId = device.DeviceId,
            Action = "EnrolDevice",
            UserId = registrar.RegistrarId,
            DeviceId = device.DeviceId,
            TransactionId = TransactionContext.Get(HttpContext)?.TransactionId
        });

        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return CreatedAtAction(nameof(Get), new { deviceId = device.DeviceId }, ToResponse(device));
    }

    [HttpGet("{deviceId}", Name = "GetDevice")]
    [ProducesResponseType(typeof(DeviceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceResponse>> Get(string deviceId)
    {
        var device = await db.Devices
            .FirstOrDefaultAsync(entry => entry.DeviceId == deviceId, HttpContext.RequestAborted);

        return device is null ? NotFoundDevice(deviceId) : ToResponse(device);
    }

    /// <summary>
    /// The facility's devices, including any that have never reported.
    ///
    /// A device enrolled weeks ago with a null lastSeenAtUtc is a deployment
    /// that failed silently: the post looks like a quiet area rather than a
    /// broken one, and this is the only place that distinction is visible.
    /// </summary>
    [HttpGet(Name = "GetDevices")]
    [ProducesResponseType(typeof(IReadOnlyList<DeviceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<DeviceResponse>>> List([FromQuery] Guid? facilityId = null)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var query = db.Devices.AsQueryable();

        if (facilityId is { } scope)
        {
            if (!await currentRegistrar.CanActForFacilityAsync(registrar, scope, HttpContext.RequestAborted))
            {
                return ApiErrors.Result(ApiErrors.Single(
                    StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                    "facilityId", $"You are not permitted to view devices for facility '{scope}'."));
            }

            query = query.Where(device => device.FacilityId == scope);
        }
        else
        {
            // No facility named: the caller's whole reach, which for a district
            // officer is their own county and for the Ministry is the country.
            // Previously this listed every device in the country to anyone who
            // could enrol one.
            var reach = await scopes.ResolveAsync(User, registrar, null, HttpContext.RequestAborted);
            if (!reach.IsAllowed)
            {
                return ApiErrors.Result(ApiErrors.Single(
                    StatusCodes.Status403Forbidden, reach.Title, reach.Field, reach.Message));
            }

            if (reach.Scope.CountyCode is { } county)
            {
                query = query.Where(device => device.Facility!.CountyCode == county);
            }
        }

        var devices = await query
            .OrderBy(device => device.FacilityId)
            .ThenBy(device => device.DeviceId)
            .ToListAsync(HttpContext.RequestAborted);

        return devices.Select(ToResponse).ToList();
    }

    /// <summary>
    /// Bars a device temporarily. Reversible, because a mislaid tablet
    /// usually reappears and a district that must re-enrol every time will
    /// stop reporting them missing.
    /// </summary>
    [HttpPost("{deviceId}/suspend", Name = "SuspendDevice")]
    [ProducesResponseType(typeof(DeviceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public Task<ActionResult<DeviceResponse>> Suspend(string deviceId, ApiRequest<ChangeDeviceStatusRequest> envelope)
        => ChangeStatusAsync(deviceId, DeviceStatus.Suspended, envelope.Data.Reason, "SuspendDevice");

    /// <summary>Returns a suspended device to service.</summary>
    [HttpPost("{deviceId}/reinstate", Name = "ReinstateDevice")]
    [ProducesResponseType(typeof(DeviceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public Task<ActionResult<DeviceResponse>> Reinstate(string deviceId, ApiRequest<ChangeDeviceStatusRequest> envelope)
        => ChangeStatusAsync(deviceId, DeviceStatus.Enrolled, envelope.Data.Reason, "ReinstateDevice");

    /// <summary>
    /// Withdraws a device permanently. There is no un-revoke: if a device
    /// was revoked in error the remedy is a fresh enrolment with a fresh
    /// key, which leaves both acts visible.
    /// </summary>
    [HttpPost("{deviceId}/revoke", Name = "RevokeDevice")]
    [ProducesResponseType(typeof(DeviceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public Task<ActionResult<DeviceResponse>> Revoke(string deviceId, ApiRequest<ChangeDeviceStatusRequest> envelope)
        => ChangeStatusAsync(deviceId, DeviceStatus.Revoked, envelope.Data.Reason, "RevokeDevice");

    private async Task<ActionResult<DeviceResponse>> ChangeStatusAsync(
        string deviceId,
        DeviceStatus status,
        string reason,
        string action)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Reason required.",
                "data.reason", "A reason is required so the next officer can tell why this device was barred."));
        }

        var device = await db.Devices
            .FirstOrDefaultAsync(entry => entry.DeviceId == deviceId, HttpContext.RequestAborted);

        if (device is null)
        {
            return NotFoundDevice(deviceId);
        }

        if (!await currentRegistrar.CanActForFacilityAsync(registrar, device.FacilityId, HttpContext.RequestAborted))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "deviceId", $"You are not permitted to manage devices for facility '{device.FacilityId}'."));
        }

        if (device.Status == DeviceStatus.Revoked)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Device is revoked.",
                "deviceId",
                $"Device '{deviceId}' was revoked and cannot change status. Enrol a replacement instead."));
        }

        if (device.Status == status)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "No change.",
                "deviceId", $"Device '{deviceId}' is already {status.ToString().ToLowerInvariant()}."));
        }

        device.Status = status;
        device.StatusReason = reason;
        device.StatusChangedAtUtc = DateTime.UtcNow;
        device.StatusChangedByRegistrarId = registrar.RegistrarId;

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Device),
            CountyCode = await districts.ForFacilityAsync(device.FacilityId, HttpContext.RequestAborted),
            EntityId = device.DeviceId,
            Action = action,
            UserId = registrar.RegistrarId,
            DeviceId = device.DeviceId,
            TransactionId = TransactionContext.Get(HttpContext)?.TransactionId
        });

        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return ToResponse(device);
    }

    private ActionResult<T> NotProvisioned<T>()
        => ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status403Forbidden, "Account not provisioned.",
            "registrar", "This account is not linked to a registrar in the registry."));

    /// <summary>
    /// Resolves acknowledgers in one query rather than per row, then maps.
    ///
    /// A name and not only an id, because "acknowledged" without a person is
    /// an undertaking nobody can be asked about — see the remark on
    /// <see cref="DeviceAlertResponse.AcknowledgedByRegistrarId"/>.
    /// </summary>
    private async Task<List<DeviceAlertResponse>> AlertResponsesAsync(
        List<DeviceAlert> alerts, CancellationToken cancellationToken)
    {
        var ids = alerts
            .Where(alert => alert.AcknowledgedByRegistrarId is not null)
            .Select(alert => alert.AcknowledgedByRegistrarId!.Value)
            .Distinct()
            .ToList();

        var names = ids.Count == 0
            ? []
            : await db.Registrars
                .Where(registrar => ids.Contains(registrar.RegistrarId))
                .ToDictionaryAsync(
                    registrar => registrar.RegistrarId,
                    registrar => registrar.DisplayName,
                    cancellationToken);

        return alerts.Select(alert => AlertResponse(alert, NameOf(alert, names))).ToList();
    }

    private static string? NameOf(DeviceAlert alert, Dictionary<Guid, string> names)
        => alert.AcknowledgedByRegistrarId is { } id && names.TryGetValue(id, out var name)
            ? name
            : null;

    private static DeviceAlertResponse AlertResponse(DeviceAlert alert, string? acknowledgedBy)
        => new(
            alert.DeviceAlertId,
            alert.DeviceId,
            alert.FacilityId,
            alert.CountyCode,
            alert.Kind,
            alert.Status,
            alert.RaisedAtUtc,
            alert.LastSeenAtUtc,
            alert.DaysSilentWhenRaised,
            alert.ThresholdDays,
            alert.AcknowledgedAtUtc,
            alert.AcknowledgedByRegistrarId,
            acknowledgedBy,
            alert.AcknowledgementNote,
            alert.ResolvedAtUtc);

    private ActionResult<DeviceResponse> NotFoundDevice(string deviceId)
        => ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status404NotFound, "Device not found.",
            "deviceId", $"No device is enrolled with id '{deviceId}'."));

    private static DeviceResponse ToResponse(Device device)
        => new(
            device.DeviceId,
            device.FacilityId,
            device.Status,
            device.Label,
            device.EnrolledAtUtc,
            device.LastSeenAtUtc,
            device.StatusChangedAtUtc,
            device.StatusReason);
}
