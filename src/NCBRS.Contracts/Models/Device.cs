namespace NCBRS.Models;

public enum DeviceStatus
{
    /// <summary>In service. May sync.</summary>
    Enrolled,

    /// <summary>
    /// Temporarily barred -- mislaid, or under investigation. Reversible,
    /// because most "lost" tablets turn up in a drawer, and forcing a
    /// re-enrolment for that would push districts to leave devices enrolled
    /// rather than report them missing.
    /// </summary>
    Suspended,

    /// <summary>
    /// Permanently withdrawn: stolen, destroyed or decommissioned. The row
    /// is kept and the id is never reissued, so a batch arriving from it
    /// later is refused with an explanation rather than looking unknown.
    /// </summary>
    Revoked
}

/// <summary>
/// A facility device permitted to sync (WS-B9, draft 6.7).
///
/// Before this existed, <c>deviceId</c> was a string the caller asserted:
/// any account with registration rights could upload an outbox under any
/// device name, and the centre had no list of which devices were real. The
/// audit trail recorded whatever was typed.
///
/// Two separate properties matter, and it is worth keeping them apart:
///
/// **Enrolment** says this device id is one the Ministry issued, to this
/// facility, and has not been withdrawn. That closes the "any string" hole.
///
/// **Possession** says the request actually came from that device, proved by
/// a signature over the request body that only the holder of the private key
/// could produce. That is what makes a stolen token insufficient on its own.
///
/// The key is held on the device and never leaves it; the centre stores only
/// the public half, exactly as it does for retired certificate signing keys.
/// </summary>
public class Device
{
    /// <summary>
    /// The device's identifier, as it appears in every sync batch and audit
    /// row. A natural key rather than a surrogate: it is what the device
    /// knows itself by offline, and what is already recorded against every
    /// registration made in the last two years.
    /// </summary>
    public required string DeviceId { get; set; }

    /// <summary>
    /// The facility this device belongs to. A device is bound to exactly one,
    /// because BRN blocks are granted per facility -- a device syncing to a
    /// second facility would be consuming numbers from a range it was never
    /// granted.
    /// </summary>
    public Guid FacilityId { get; set; }

    public Facility? Facility { get; set; }

    public DeviceStatus Status { get; set; } = DeviceStatus.Enrolled;

    /// <summary>
    /// The device's public key, SubjectPublicKeyInfo PEM. ECDSA P-256, the
    /// same curve the certificate signing side uses, so there is one
    /// algorithm choice in the system rather than two.
    /// </summary>
    public required string PublicKeyPem { get; set; }

    /// <summary>A human label -- "Terekeka post, tablet 2" -- for the district officer.</summary>
    public string? Label { get; set; }

    public DateTime EnrolledAtUtc { get; set; } = DateTime.UtcNow;

    public Guid EnrolledByRegistrarId { get; set; }

    /// <summary>
    /// When this device last successfully synced, or null if it never has.
    ///
    /// Null is the important value. A device enrolled six weeks ago that has
    /// never once reported is the failure nobody notices: the post looks
    /// like a quiet area rather than a broken deployment, and the dashboard
    /// cannot tell the difference without this row existing.
    /// </summary>
    public DateTime? LastSeenAtUtc { get; set; }

    public DateTime? StatusChangedAtUtc { get; set; }

    public Guid? StatusChangedByRegistrarId { get; set; }

    /// <summary>Why it was suspended or revoked. Required for either.</summary>
    public string? StatusReason { get; set; }
}
