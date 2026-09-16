namespace NCBRS.Models;

/// <summary>
/// Enrols a device. The public key is generated on the device and only its
/// public half travels -- the centre must never be in a position to sign as
/// a device, or "the device sent this" stops meaning anything.
/// </summary>
public record EnrolDeviceRequest
{
    public string DeviceId { get; init; } = string.Empty;

    public Guid FacilityId { get; init; }

    /// <summary>SubjectPublicKeyInfo PEM for an ECDSA P-256 key.</summary>
    public string PublicKeyPem { get; init; } = string.Empty;

    public string? Label { get; init; }
}

/// <summary>
/// Suspends or revokes a device. A reason is required for both: a device
/// barred with no explanation leaves the next officer unable to tell a
/// stolen tablet from one withdrawn at end of life.
/// </summary>
public record ChangeDeviceStatusRequest
{
    public string Reason { get; init; } = string.Empty;
}

public record DeviceResponse(
    string DeviceId,
    Guid FacilityId,
    DeviceStatus Status,
    string? Label,
    DateTime EnrolledAtUtc,

    /// <summary>Null when the device has never synced -- see <see cref="Device.LastSeenAtUtc"/>.</summary>
    DateTime? LastSeenAtUtc,

    DateTime? StatusChangedAtUtc,
    string? StatusReason);
