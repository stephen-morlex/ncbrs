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
    SignatureFailed
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
