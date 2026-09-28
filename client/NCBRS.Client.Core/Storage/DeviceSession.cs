using NCBRS.Client.Brn;
using NCBRS.Client.Certificates;
using NCBRS.Client.Network;
using NCBRS.Client.Sync;

namespace NCBRS.Client.Storage;

/// <summary>
/// The core rebuilt from a saved <see cref="DeviceState"/>, and captured back
/// into one. The shell restores a session after the registrar unlocks, drives
/// <see cref="Facility"/> and a <see cref="ConnectivityWindow"/> over
/// <see cref="Sync"/>, and after every act calls <see cref="Capture"/> and saves.
///
/// <see cref="FacilityClient"/> keeps its allocator and outbox private, so the
/// session holds its own references to them — that is what makes capture
/// possible without the client exposing its internals.
/// </summary>
public sealed class DeviceSession : IDisposable
{
    private readonly DeviceBrnAllocator _brn;
    private readonly SyncOutbox _outbox;

    private DeviceSession(
        DeviceIdentity identity, DeviceSigner signer, DeviceBrnAllocator brn, SyncOutbox outbox,
        FacilityClient facility, ClientSyncState sync)
    {
        Identity = identity;
        Signer = signer;
        _brn = brn;
        _outbox = outbox;
        Facility = facility;
        Sync = sync;
    }

    public DeviceIdentity Identity { get; }

    public DeviceSigner Signer { get; }

    public FacilityClient Facility { get; }

    /// <summary>The in-flight upload and the verification bundle, for <see cref="ConnectivityWindow"/>.</summary>
    public ClientSyncState Sync { get; }

    /// <summary>
    /// Whether the state holds what registering needs: an identity, a key and a
    /// block. Until then the tablet is still being handed over.
    /// </summary>
    public static bool CanRestore(DeviceState state)
        => state.Identity is not null && state.DevicePrivateKeyPem is not null && state.Brn is not null;

    public static DeviceSession Restore(DeviceState state, long lowBlockThreshold = 50)
    {
        if (state.Identity is not { } identity || state.DevicePrivateKeyPem is not { } key || state.Brn is not { } brn)
        {
            throw new InvalidOperationException(
                "The device is not provisioned yet: it needs an enrolled identity, its key and a block of numbers.");
        }

        var signer = DeviceSigner.FromPrivateKey(key);
        var allocator = new DeviceBrnAllocator(
            identity.DeviceId, brn.BlockStart, brn.BlockEnd, brn.NextAvailable,
            brn.ProvisionalSequence, brn.PendingBlockStart, brn.PendingBlockEnd);
        var outbox = new SyncOutbox(identity.DeviceId, identity.FacilityId, state.Outbox);
        var facility = new FacilityClient(identity.DeviceId, identity.FacilityId, allocator, outbox, signer, lowBlockThreshold);

        var sync = new ClientSyncState
        {
            InFlight = state.InFlight,
            Bundle = state.Bundle is { } bundle
                ? CachedVerificationBundle.From(bundle.SigningKeys, bundle.RevocationLists, bundle.FetchedAtUtc)
                : CachedVerificationBundle.Empty,
        };

        return new DeviceSession(identity, signer, allocator, outbox, facility, sync);
    }

    /// <summary>
    /// Write the session's current state into <paramref name="state"/>, leaving
    /// what the session does not own (the offline token, the PIN) untouched.
    /// </summary>
    public DeviceState Capture(DeviceState state)
    {
        state.Identity = Identity;
        state.DevicePrivateKeyPem = Signer.ExportPrivateKeyPem();
        state.Brn = new BrnState(
            _brn.BlockStart, _brn.BlockEnd, _brn.NextAvailable, _brn.ProvisionalSequence,
            _brn.PendingBlockStart, _brn.PendingBlockEnd);
        state.Outbox = [.. _outbox.Pending];
        state.InFlight = Sync.InFlight;
        state.Bundle = Sync.Bundle is { HasBundle: true, FetchedAtUtc: { } fetchedAt } bundle
            ? new BundleState([.. bundle.SigningKeys], [.. bundle.RevocationLists], fetchedAt)
            : null;
        return state;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(Sync.Bundle, CachedVerificationBundle.Empty))
        {
            Sync.Bundle.Dispose();
        }
    }
}
