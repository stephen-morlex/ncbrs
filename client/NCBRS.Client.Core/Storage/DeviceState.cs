using NCBRS.Certificates;
using NCBRS.Client.Auth;
using NCBRS.Client.Sync;
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
    /// Births the registry has confirmed, kept 30 days for the Records list and
    /// then forgotten (<see cref="RecentBirths"/>). Empty in a state saved
    /// before it existed.
    /// </summary>
    public List<RecentBirth> Recent { get; set; } = [];

    /// <summary>When a sync last reached the registry, for "last sync" on the home screen. Null until one has.</summary>
    public DateTime? LastSyncedAtUtc { get; set; }

    /// <summary>
    /// The births in <see cref="Outbox"/> the centre refused, by BRN, with its
    /// reasons. Held until corrected; lost, a restarted tablet would resend them
    /// unchanged and have them refused again, every window.
    /// </summary>
    public Dictionary<string, List<ApiError>> Refused { get; set; } = [];

    /// <summary>
    /// The upload sent, or about to be, and not yet settled. Kept byte for
    /// byte: the next window resends these exact bytes under the same
    /// transaction id, never a rebuilt batch.
    /// </summary>
    public SignedUpload? InFlight { get; set; }

    /// <summary>What certificates are verified against offline (B8). Null until first fetched.</summary>
    public BundleState? Bundle { get; set; }

    /// <summary>The registry's transfer key, from the bundle: what USB exports are sealed to.</summary>
    public TransferKeyResponse? TransferKey { get; set; }

    /// <summary>
    /// The last export to removable media: when, and which births. They stay
    /// queued until the registry confirms them; this is what lets the tablet
    /// say which births are on a stick somewhere and not yet confirmed.
    /// </summary>
    public ExportRecord? LastExport { get; set; }

    /// <summary>The registrar's Keycloak offline token. Rotated on every renewal, so saved on every renewal.</summary>
    public string? OfflineToken { get; set; }

    /// <summary>
    /// The facility's registrars and their PIN credentials, provisioned from the
    /// centre, so any of them can unlock this tablet offline and the births
    /// they register are credited to them.
    /// </summary>
    public List<StaffCredential> Staff { get; set; } = [];

    /// <summary>When the staff credentials were issued by the centre.</summary>
    public DateTime? StaffIssuedAtUtc { get; set; }

    /// <summary>
    /// Wrong PINs, counted for the <em>device</em>, not per person: per-person
    /// counters would give a thief five guesses for every name on the list.
    /// </summary>
    public PinAttempts Attempts { get; set; } = new(0, null);
}

/// <summary>
/// Fixed at handover: the enrolled device, its facility, where it reaches the
/// system, and the identity realm its registrars sign in to.
/// </summary>
public sealed record DeviceIdentity(
    string DeviceId, Guid FacilityId, Uri Centre, Uri? SyncVia = null, Uri? Realm = null, string? FacilityName = null)
{
    /// <summary>
    /// A device id derived from the device's own public key. Derived rather
    /// than random so a handover interrupted between enrolling and saving
    /// retries under the same id: the centre's 409 then means "this tablet is
    /// already enrolled", not an identity lost with a new one to enrol.
    /// 48 bits of the key's hash; a national fleet is thousands of tablets.
    /// </summary>
    public static string IdFor(string publicKeyPem)
        => "TAB-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(publicKeyPem.Trim())))[..12];
}

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
    long? PendingBlockEnd,
    // Composed blocks only. Defaulted so a state saved before the composed
    // format loads as the numeric block it was.
    string? OfficeCode = null,
    int? Year = null,
    string? PendingOfficeCode = null,
    int? PendingYear = null);

/// <summary>The held verification bundle: public keys and signed lists, nothing secret.</summary>
public sealed record BundleState(
    IReadOnlyList<VerificationKey> SigningKeys,
    IReadOnlyList<CertificateRevocationList> RevocationLists,
    DateTime FetchedAtUtc);

/// <summary>A registrar who may unlock this tablet, and the credential they unlock it with.</summary>
public sealed record StaffCredential(Guid RegistrarId, string DisplayName, RegistrarRole Role, PinCredential Credential);

/// <summary>The device's unlock attempt state, saved after <em>every</em> attempt.</summary>
public sealed record PinAttempts(int FailedAttempts, DateTime? LockedUntilUtc);

/// <summary>A sealed transfer file written for removable media, and the births on it.</summary>
public sealed record ExportRecord(DateTime AtUtc, List<string> Brns);
