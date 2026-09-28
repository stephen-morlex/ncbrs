using NCBRS.Certificates;
using NCBRS.Client.Auth;
using NCBRS.Models;

namespace NCBRS.Client.Storage;

/// <summary>
/// Everything the tablet must still know after a restart, a flat battery or a
/// week off: the one record the encrypted store (B2) writes. The core's objects
/// are state machines that do no I/O; this is their state, captured by
/// <see cref="DeviceSession.Capture"/> and rebuilt by <see cref="DeviceSession.Restore"/>.
///
/// Two of these are not optional to lose. Losing the BRN cursor re-hands
/// numbers already printed on slips; losing the PIN attempt count resets the
/// brute-force limit. That is why the store saves after every act, not on exit.
/// </summary>
public sealed class DeviceState
{
    /// <summary>The shape of this record, so a later app version can read an older one.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Who this tablet is and where it syncs. Null until handover.</summary>
    public DeviceIdentity? Identity { get; set; }

    /// <summary>The device's signing key. Generated once, never sent anywhere.</summary>
    public string? DevicePrivateKeyPem { get; set; }

    /// <summary>The granted block and the cursor into it. Null until the first block is granted.</summary>
    public BrnState? Brn { get; set; }

    /// <summary>Births registered and not yet settled with the centre.</summary>
    public List<SyncBirthRecord> Outbox { get; set; } = [];

    /// <summary>
    /// The upload sent, or about to be, and not yet settled. Kept byte for
    /// byte: the next window resends these exact bytes under the same
    /// transaction id, never a rebuilt batch.
    /// </summary>
    public SignedUpload? InFlight { get; set; }

    /// <summary>What certificates are verified against offline (B8). Null until first fetched.</summary>
    public BundleState? Bundle { get; set; }

    /// <summary>The registrar's Keycloak offline token. Rotated on every renewal, so saved on every renewal.</summary>
    public string? OfflineToken { get; set; }

    /// <summary>The registrar's PIN and how many wrong guesses have been made against it.</summary>
    public PinState? Pin { get; set; }
}

/// <summary>Fixed at handover: the enrolled device, its facility, and where it reaches the system.</summary>
public sealed record DeviceIdentity(string DeviceId, Guid FacilityId, Uri Centre, Uri? SyncVia = null);

/// <summary>
/// The allocator's whole state. <see cref="BlockStart"/> and <see cref="BlockEnd"/>
/// are here, not just the cursor: rolling over to a staged block replaces the
/// current block, so a device that saved only the cursor would restore it
/// against the old block's range.
/// </summary>
public sealed record BrnState(
    long BlockStart,
    long BlockEnd,
    long NextAvailable,
    long ProvisionalSequence,
    long? PendingBlockStart,
    long? PendingBlockEnd);

/// <summary>The held verification bundle: public keys and signed lists, nothing secret.</summary>
public sealed record BundleState(
    IReadOnlyList<VerificationKey> SigningKeys,
    IReadOnlyList<CertificateRevocationList> RevocationLists,
    DateTime FetchedAtUtc);

/// <summary>
/// The registrar the PIN unlocks, their PIN credential, and the attempt state,
/// saved after <em>every</em> attempt.
/// </summary>
public sealed record PinState(
    Guid? RegistrarId,
    PinCredential Credential,
    int FailedAttempts,
    DateTime? LockedUntilUtc);
