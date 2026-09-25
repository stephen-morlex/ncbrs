using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Models;

namespace NCBRS.Services;

public enum DeviceCheckOutcome
{
    /// <summary>Enrolled, active, bound to this facility, and it proved possession.</summary>
    Accepted,

    /// <summary>No such device id has ever been enrolled.</summary>
    NotEnrolled,

    /// <summary>Enrolled, but suspended or revoked.</summary>
    NotActive,

    /// <summary>Enrolled to a different facility than the batch claims.</summary>
    WrongFacility,

    /// <summary>The body was not signed by this device's enrolled key.</summary>
    SignatureFailed,

    /// <summary>
    /// The request claims a channel its token was not issued for: a browser
    /// session presenting itself as a device, or a device presenting itself
    /// as the management site to avoid proving which device it is.
    /// </summary>
    WrongChannel
}

public record DeviceCheck(DeviceCheckOutcome Outcome, string Detail, Device? Device = null)
{
    public bool Accepted => Outcome == DeviceCheckOutcome.Accepted;
}

/// <summary>
/// Settings for WS-B9 enforcement.
/// </summary>
public class DeviceEnrolmentOptions
{
    public const string SectionName = "DeviceEnrolment";

    /// <summary>
    /// Whether an unenrolled device is refused.
    ///
    /// Defaults to **on**. A security control that ships disabled is a
    /// security control that stays disabled: the deployment that most needs
    /// it is the one that will never get round to the config change. Turning
    /// it off is a deliberate, visible act -- which is what a migration
    /// window should be.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// Whether a batch must additionally be signed by the device's key.
    ///
    /// Separate from <see cref="Required"/> because they close different
    /// holes and a fleet adopts them at different times: a registry of
    /// devices can be populated from existing records, while signing needs
    /// every tablet to hold a key. Enrolment without signatures is still
    /// worth having, and is honest about being an allowlist rather than
    /// proof of possession.
    /// </summary>
    public bool RequireSignature { get; set; } = true;

    /// <summary>
    /// The OIDC client id of the management site, and the device id it
    /// registers under. A token issued to this client comes from an
    /// interactive browser sign-in (the realm allows it no password grant),
    /// so it is the web channel; every other client is a device channel and
    /// must prove which device it is. Must match the Keycloak realm.
    /// </summary>
    public string WebClientId { get; set; } = "ncbrs-web";
}

/// <summary>
/// Enrols facility devices and decides whether one may sync (WS-B9).
///
/// Before this, <c>deviceId</c> was whatever the caller typed. The audit
/// trail recorded it faithfully and it meant nothing -- which matters
/// because that trail is the evidence behind a legal record.
/// </summary>
public class DeviceEnrolmentService(NcbrsDbContext db, DeviceEnrolmentOptions options)
{
    /// <summary>
    /// Whether this device may submit this batch, and whether it proved it.
    /// </summary>
    public async Task<DeviceCheck> CheckAsync(
        string deviceId,
        Guid facilityId,
        ReadOnlyMemory<byte> body,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        var device = await db.Devices
            .FirstOrDefaultAsync(entry => entry.DeviceId == deviceId, cancellationToken);

        if (device is null)
        {
            return options.Required
                ? new DeviceCheck(DeviceCheckOutcome.NotEnrolled,
                    $"Device '{deviceId}' is not enrolled. A district officer must enrol it before it can sync.")
                : new DeviceCheck(DeviceCheckOutcome.Accepted, "Enrolment is not enforced.");
        }

        // Past this point the device is known, so its status is honoured even
        // where enrolment is not being enforced. A revoked device is a
        // decision someone made deliberately, and a configuration flag
        // meaning "let stolen tablets back in" would be a trap.
        if (device.Status != DeviceStatus.Enrolled)
        {
            return new DeviceCheck(DeviceCheckOutcome.NotActive,
                $"Device '{deviceId}' is {device.Status.ToString().ToLowerInvariant()}"
                + $"{(device.StatusReason is null ? "" : $": {device.StatusReason}")}.",
                device);
        }

        if (device.FacilityId != facilityId)
        {
            // Not a data problem to sort out per record: a device is bound to
            // the facility whose BRN block it draws from, so numbers in this
            // batch come from a range this facility was never granted.
            return new DeviceCheck(DeviceCheckOutcome.WrongFacility,
                $"Device '{deviceId}' is enrolled to a different facility.",
                device);
        }

        if (!options.RequireSignature)
        {
            return new DeviceCheck(DeviceCheckOutcome.Accepted, "Signature not enforced.", device);
        }

        var verified = DeviceSignature.Verify(device.PublicKeyPem, body.Span, signature);

        return verified.Valid
            ? new DeviceCheck(DeviceCheckOutcome.Accepted, "Verified.", device)
            : new DeviceCheck(DeviceCheckOutcome.SignatureFailed, verified.Reason!, device);
    }

    /// <summary>
    /// Which channel a write came through, decided by the token rather than
    /// by the body. Applied by <see cref="DeviceChannelGate"/> to every write
    /// that names a device.
    ///
    /// The body's device id is a claim anyone holding a registrar's token can
    /// type. The token's authorised party (`azp`) is not: it names the client
    /// the identity provider issued the token to. So:
    ///
    /// - a **web-client** token is the management site, which acts as the web
    ///   channel and holds no device key -- the user's interactive session is
    ///   the authority. It may not claim to be a device. Where the field is
    ///   optional (a BRN block request) it may name no device at all.
    /// - **any other** token is a device channel, held to the same rule as a
    ///   sync batch: enrolled, active, at this facility, and (when enforced)
    ///   signed over the exact bytes sent. It must name itself, and may not
    ///   claim to be the management site -- or a stolen device token could skip
    ///   the signature by saying it came from a browser.
    ///
    /// Before this, every write but sync took the device id on trust, so a
    /// stolen token could act as any device -- including a revoked one -- and
    /// the audit trail would record whatever was typed.
    /// </summary>
    public async Task<DeviceCheck> CheckChannelAsync(
        ClaimsPrincipal user,
        string? deviceId,
        Guid facilityId,
        ReadOnlyMemory<byte> body,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        var client = user.FindFirstValue("azp");
        var fromWeb = string.Equals(client, options.WebClientId, StringComparison.Ordinal);
        var named = !string.IsNullOrWhiteSpace(deviceId);
        var claimsWeb = string.Equals(deviceId, options.WebClientId, StringComparison.Ordinal);

        if (fromWeb)
        {
            return claimsWeb || !named
                ? new DeviceCheck(DeviceCheckOutcome.Accepted, "Acting through the management site.")
                : new DeviceCheck(DeviceCheckOutcome.WrongChannel,
                    $"A signed-in browser session cannot act as device '{deviceId}'; "
                    + $"the management site acts as '{options.WebClientId}'.");
        }

        if (claimsWeb)
        {
            return new DeviceCheck(DeviceCheckOutcome.WrongChannel,
                $"'{options.WebClientId}' is the management site's channel. A device acts as itself, "
                + "with its own signature.");
        }

        if (!named)
        {
            return new DeviceCheck(DeviceCheckOutcome.NotEnrolled,
                "A device must name itself, so the register can say which device acted.");
        }

        return await CheckAsync(deviceId!, facilityId, body, signature, cancellationToken);
    }

    /// <summary>
    /// Records that a device reached the centre. This, and its absence, is
    /// what makes a post that has never reported visible at all.
    ///
    /// Any outstanding silence alert is resolved here rather than waiting for
    /// the next sweep: a district officer looking at the queue minutes after
    /// a post came back should not still be told to drive out there.
    /// </summary>
    public async Task MarkSeenAsync(Device? device, CancellationToken cancellationToken = default)
    {
        if (device is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        device.LastSeenAtUtc = now;

        var outstanding = await db.DeviceAlerts
            .Where(alert => alert.DeviceId == device.DeviceId && alert.ResolvedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var alert in outstanding)
        {
            // Resolution is the device reporting, never an officer saying so.
            alert.Status = DeviceAlertStatus.Resolved;
            alert.ResolvedAtUtc = now;
        }
    }
}
