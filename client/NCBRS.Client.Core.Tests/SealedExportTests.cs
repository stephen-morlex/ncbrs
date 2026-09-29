using System.Security.Cryptography;
using System.Text.Json;
using NCBRS.Client.Network;
using NCBRS.Client.Storage;
using NCBRS.Client.Sync;
using NCBRS.Devices;
using NCBRS.Models;
using Xunit;
using static NCBRS.Client.Tests.CentralClientTests;

namespace NCBRS.Client.Tests;

/// <summary>
/// Exporting births to a USB stick or card, sealed to the registry. The tablet
/// must already hold the registry's key when it has no signal — it gets it
/// from the bundle it refreshes — and what it writes must be something only
/// the registry can open, carrying the births still waiting to sync.
/// </summary>
public sealed class SealedExportTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Facility = Guid.Parse("0199c000-0000-7000-8000-0000000f0003");
    private const string Device = "TAB-0A1B2C3D4E5F";

    private static readonly (string Private, string Public) RegistryKey = SealedTransfer.GenerateKeyPair();
    private static readonly TransferKeyResponse Published = new("moh-transfer-2026", "ECDH-P256+HKDF-SHA256+AES-256-GCM", RegistryKey.Public);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ncbrs-export-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RegisterBirthRequest Birth(string name)
        => new() { ChildFullName = name, DateOfBirth = Now.Date.AddDays(-2), Sex = Sex.Female, RegisteredAtUtc = Now };

    private static byte[] OpenAsRegistry(byte[] file)
    {
        using var key = ECDiffieHellman.Create();
        key.ImportFromPem(RegistryKey.Private);
        var opened = SealedTransfer.Open(file, id => id == Published.KeyId ? key : null);
        Assert.True(opened.Opened, opened.Reason);
        return opened.Plaintext!;
    }

    [Fact]
    public void AnExportOpensOnlyAtTheRegistryAndCarriesTheQueuedBirthsSigned()
    {
        var signer = DeviceSigner.Generate();
        var client = new FacilityClient(Device, Facility, new Brn.DeviceBrnAllocator(Device, 100_000, 100_099),
            new SyncOutbox(Device, Facility), signer);
        var first = client.RegisterBirth(Birth("Ayen Deng")).Brn;
        var second = client.RegisterBirth(Birth("Bol Kenyi")).Brn;

        var export = client.BuildSealedTransferFile(Published);

        Assert.Equal([first, second], export.Brns);
        var envelope = TransferEnvelopes.Open(OpenAsRegistry(export.File), signer.PublicKeyPem);
        Assert.True(envelope.Accepted);
        Assert.Equal(Device, envelope.DeviceId);
        Assert.DoesNotContain("Ayen", System.Text.Encoding.UTF8.GetString(export.File));

        // The stick may be lost: nothing leaves the queue until the registry confirms it.
        Assert.Equal(2, client.PendingCount);
    }

    /// <summary>A birth held for correction is not exported: it would only be refused again.</summary>
    [Fact]
    public void ABirthHeldForCorrectionIsLeftOff()
    {
        var client = new FacilityClient(Device, Facility, new Brn.DeviceBrnAllocator(Device, 100_000, 100_099),
            new SyncOutbox(Device, Facility), DeviceSigner.Generate());
        var refused = client.RegisterBirth(Birth("Ayen Deng")).Brn;
        var queued = client.RegisterBirth(Birth("Bol Kenyi")).Brn;
        client.Settle(new SyncBatchResponse(Guid.NewGuid(), SyncBatchStatus.Reconciled, 1, 0, 0, 1,
            [new SyncRecordOutcome(refused, SyncRecordStatus.Rejected, [new ApiError("record", "refused")])]));

        Assert.Equal([queued], client.BuildSealedTransferFile(Published).Brns);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Bundle(TransferKeyResponse? transferKey)
        => _ => FakeNetwork.Envelope(JsonSerializer.Serialize(new
        {
            keyId = "moh-2026",
            algorithm = "ECDSA-P256-SHA256",
            publicKeyPem = "-----BEGIN CERTIFICATE-----",
            revocations = new
            {
                issuer = "NCBRS", version = "v1", keyId = "moh-2026", coversFromUtc = (DateTime?)null,
                issuedAtUtc = Now, nextUpdateUtc = Now.AddDays(7), count = 0, entries = Array.Empty<object>(), signature = "sig",
            },
            keys = new[] { new { keyId = "moh-2026", publicKeyPem = "-----BEGIN CERTIFICATE-----", active = true } },
            transferKey,
        }, ClientJson.Options));

    private static FacilityClient Idle()
        => new(Device, Facility, new Brn.DeviceBrnAllocator(Device, 100_000, 100_099), new SyncOutbox(Device, Facility), DeviceSigner.Generate());

    /// <summary>The tablet exports exactly when it has no signal, so it must already hold the key from its last window.</summary>
    [Fact]
    public async Task TheKeyArrivesWithTheBundleAndSurvivesARestart()
    {
        var state = new ClientSyncState();
        await new ConnectivityWindow(Idle(), CentreClient(new FakeNetwork { Respond = Bundle(Published) }), DeviceSigner.Generate(),
            (_, _) => Task.CompletedTask).RunAsync(state, Now);

        Assert.Equal(Published, state.TransferKey);

        var store = new EncryptedStateFile(Path.Combine(_directory, "device.state"), EncryptedStateFile.NewKey());
        var device = new DeviceState
        {
            Identity = new DeviceIdentity(Device, Facility, new Uri("https://registry.ncbrs.ss/")),
            DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem(),
            Brn = new BrnState(100_000, 100_099, 100_000, 0, null, null),
        };
        using (var session = DeviceSession.Restore(device))
        {
            session.Sync.TransferKey = state.TransferKey;
            await store.SaveAsync(session.Capture(device));
        }

        using var restored = DeviceSession.Restore((await store.LoadAsync())!);
        Assert.Equal(Published, restored.Sync.TransferKey);
    }

    /// <summary>
    /// A tablet upgraded with a fresh bundle but no transfer key would otherwise
    /// wait days for the next due refresh, unable to export in the meantime.
    /// </summary>
    [Fact]
    public async Task ATabletWithAFreshBundleButNoKeyFetchesOneStraightAway()
    {
        var fresh = Certificates.CachedVerificationBundle.From(
            [new NCBRS.Certificates.VerificationKey("moh-2026", "-----BEGIN CERTIFICATE-----", true)],
            [new CertificateRevocationList("NCBRS", "v1", "moh-2026", null, Now, Now.AddDays(7), 0, [], "sig")],
            Now);
        var state = new ClientSyncState { Bundle = fresh };
        Assert.False(fresh.RefreshDue(Now, TimeSpan.FromDays(2)));

        var report = await new ConnectivityWindow(Idle(), CentreClient(new FakeNetwork { Respond = Bundle(Published) }), DeviceSigner.Generate(),
            (_, _) => Task.CompletedTask).RunAsync(state, Now);

        Assert.True(report.BundleRefreshed);
        Assert.Equal(Published, state.TransferKey);
    }

    /// <summary>A bundle from a registry that sends no key does not take away the one held.</summary>
    [Fact]
    public async Task ABundleWithoutAKeyKeepsTheOneHeld()
    {
        var state = new ClientSyncState { TransferKey = Published };

        await new ConnectivityWindow(Idle(), CentreClient(new FakeNetwork { Respond = Bundle(null) }), DeviceSigner.Generate(),
            (_, _) => Task.CompletedTask).RunAsync(state, Now);

        Assert.Equal(Published, state.TransferKey);
    }
}
