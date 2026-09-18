namespace NCBRS.Models;

/// <summary>
/// Sets or changes the caller's own offline device PIN. There is no
/// registrar id: a PIN belongs to the authenticated caller and nobody else,
/// so it is taken from the token.
/// </summary>
public record SetDevicePinRequest
{
    public string Pin { get; init; } = string.Empty;

    /// <summary>
    /// Required when replacing an existing PIN, so possession of an unlocked
    /// session is not enough to lock a colleague out of their own device.
    /// </summary>
    public string? CurrentPin { get; init; }
}

public record SetDevicePinResponse(
    Guid RegistrarId,
    string DisplayName,
    DateTime UpdatedAtUtc
);

/// <summary>
/// One registrar's offline credential, as cached on a facility device.
///
/// Carries the hash, never the PIN. The device compares a typed PIN against
/// this locally while it has no connectivity.
/// </summary>
public record DeviceCredentialEntry(
    Guid RegistrarId,
    string DisplayName,
    RegistrarRole Role,
    string CredentialHash
);

/// <summary>
/// The bundle a device caches so it can authenticate staff offline.
///
/// IssuedAtUtc lets a device expire a stale bundle: a registrar who has left
/// is removed here, and a device that has not synced since will still let
/// them in until it does. Devices should refuse to use a bundle older than
/// the Ministry's tolerance rather than trusting it indefinitely.
/// </summary>
public record DeviceCredentialBundle(
    Guid FacilityId,
    string FacilityName,
    DateTime IssuedAtUtc,
    IReadOnlyList<DeviceCredentialEntry> Credentials
);
