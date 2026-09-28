using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NCBRS.Certificates;
using NCBRS.Client.Auth;
using NCBRS.Client.Storage;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The tablet's store at rest (WS-B2): everything survives a round trip, nothing
/// is readable without the key, a store that cannot be read is an error rather
/// than an empty device, and a save cut short leaves the previous state whole.
/// </summary>
public sealed class EncryptedStateFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ncbrs-store-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = EncryptedStateFile.NewKey();

    private string StorePath => Path.Combine(_directory, "device.state");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    internal static VerificationKey SelfSignedKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Test Ministry", ecdsa, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new VerificationKey("test-key", certificate.ExportCertificatePem(), Active: true);
    }

    internal static CertificateRevocationList ListExpiring(DateTime nextUpdateUtc)
        => new("NCBRS", "v1", "test-key", null, nextUpdateUtc.AddDays(-1), nextUpdateUtc, 0, [], "signed-by-the-centre");

    private static DeviceState FullState()
    {
        var fetched = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);
        return new DeviceState
        {
            Identity = new DeviceIdentity("TABLET-7", Guid.Parse("0199c000-0000-7000-8000-00000000f004"),
                new Uri("https://ncbrs.example/"), new Uri("https://district.example/")),
            DevicePrivateKeyPem = NCBRS.Devices.DeviceSignature.GenerateKeyPair().PrivateKeyPem,
            Brn = new BrnState(200_000, 200_199, 200_012, 3, 200_400, 200_599),
            Outbox =
            [
                new SyncBirthRecord
                {
                    RegisteredByRegistrarId = Guid.Parse("0199c000-0000-7000-8000-0000000000b1"),
                    Birth = new RegisterBirthRequest
                    {
                        Brn = "200011",
                        ChildFullName = "Nyandeng Garang",
                        DateOfBirth = new DateTime(2026, 9, 27),
                        Sex = Sex.Female,
                        BirthWeightGrams = 3100,
                        GestationalAgeWeeks = 39.5m,
                        MotherFullName = "Achol Deng",
                        MaternalStatistics = new MaternalStatisticsRequest
                        {
                            MotherEducationLevel = EducationLevel.Primary,
                            PriorLiveBirths = 2,
                            MedicalCareBeganDate = new DateOnly(2026, 2, 1),
                        },
                    },
                },
            ],
            InFlight = new SignedUpload(Encoding.UTF8.GetBytes("""{"meta":{},"data":{}}"""), "sig==",
                "X-NCBRS-Device-Signature", Guid.Parse("0199c000-0000-7000-8000-0000000000c1")),
            Bundle = new BundleState([SelfSignedKey()], [ListExpiring(fetched.AddDays(7))], fetched),
            OfflineToken = "offline-token-value",
            Pin = new PinState(Guid.Parse("0199c000-0000-7000-8000-0000000000b1"),
                OfflinePinLock.CreateCredential("2468", iterations: 1_000), 2, null),
        };
    }

    [Fact]
    public async Task NothingSavedLoadsAsNull()
    {
        var store = new EncryptedStateFile(StorePath, _key);

        Assert.False(store.Exists);
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task EverythingSurvivesARoundTrip()
    {
        var saved = FullState();
        await new EncryptedStateFile(StorePath, _key).SaveAsync(saved);

        var loaded = await new EncryptedStateFile(StorePath, _key).LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(saved.Identity, loaded.Identity);
        Assert.Equal(saved.DevicePrivateKeyPem, loaded.DevicePrivateKeyPem);
        Assert.Equal(saved.Brn, loaded.Brn);
        Assert.Equal(saved.OfflineToken, loaded.OfflineToken);
        Assert.Equal(saved.Pin, loaded.Pin);

        var birth = Assert.Single(loaded.Outbox);
        Assert.Equal(saved.Outbox[0].RegisteredByRegistrarId, birth.RegisteredByRegistrarId);
        Assert.Equal(saved.Outbox[0].Birth with { MaternalStatistics = null }, birth.Birth with { MaternalStatistics = null });
        Assert.Equal(saved.Outbox[0].Birth.MaternalStatistics, birth.Birth.MaternalStatistics);

        Assert.NotNull(loaded.InFlight);
        Assert.Equal(saved.InFlight!.Body, loaded.InFlight.Body);
        Assert.Equal(saved.InFlight.TransactionId, loaded.InFlight.TransactionId);
        Assert.Equal(saved.InFlight.Signature, loaded.InFlight.Signature);

        Assert.NotNull(loaded.Bundle);
        Assert.Equal(saved.Bundle!.FetchedAtUtc, loaded.Bundle.FetchedAtUtc);
        Assert.Equal(saved.Bundle.SigningKeys, loaded.Bundle.SigningKeys);
        var list = Assert.Single(loaded.Bundle.RevocationLists);
        Assert.Equal(saved.Bundle.RevocationLists[0].NextUpdateUtc, list.NextUpdateUtc);
        Assert.Equal(saved.Bundle.RevocationLists[0].Signature, list.Signature);
        Assert.Empty(list.Entries);
    }

    [Fact]
    public async Task TheFileHoldsNothingReadable()
    {
        await new EncryptedStateFile(StorePath, _key).SaveAsync(FullState());

        var onDisk = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(StorePath));

        Assert.DoesNotContain("Nyandeng", onDisk);
        Assert.DoesNotContain("offline-token-value", onDisk);
        Assert.DoesNotContain("PRIVATE KEY", onDisk);
    }

    [Fact]
    public async Task EverySaveDrawsAFreshNonce()
    {
        var store = new EncryptedStateFile(StorePath, _key);
        var state = FullState();

        await store.SaveAsync(state);
        var first = await File.ReadAllBytesAsync(StorePath);
        await store.SaveAsync(state);
        var second = await File.ReadAllBytesAsync(StorePath);

        Assert.NotEqual(first.AsSpan(1, 12).ToArray(), second.AsSpan(1, 12).ToArray());
    }

    /// <summary>
    /// The failure that matters most: read as "no state", the app would start
    /// as a fresh device and its first save would overwrite unsynced births.
    /// </summary>
    [Fact]
    public async Task AWrongKeyIsRefusedNeverReadAsAnEmptyDevice()
    {
        await new EncryptedStateFile(StorePath, _key).SaveAsync(FullState());

        await Assert.ThrowsAsync<StateFileUnreadableException>(
            () => new EncryptedStateFile(StorePath, EncryptedStateFile.NewKey()).LoadAsync());
    }

    [Theory]
    [InlineData(0)]    // the version byte, which the tag covers
    [InlineData(5)]    // the nonce
    [InlineData(20)]   // the tag
    [InlineData(-1)]   // the ciphertext
    public async Task AnAlteredFileIsRefused(int offset)
    {
        await new EncryptedStateFile(StorePath, _key).SaveAsync(FullState());
        var bytes = await File.ReadAllBytesAsync(StorePath);
        var at = offset < 0 ? bytes.Length + offset : offset;
        bytes[at] ^= 0x01;
        await File.WriteAllBytesAsync(StorePath, bytes);

        await Assert.ThrowsAsync<StateFileUnreadableException>(() => new EncryptedStateFile(StorePath, _key).LoadAsync());
    }

    [Fact]
    public async Task AFileTooShortToBeAStoreIsRefused()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(StorePath, [1, 2, 3]);

        await Assert.ThrowsAsync<StateFileUnreadableException>(() => new EncryptedStateFile(StorePath, _key).LoadAsync());
    }

    /// <summary>
    /// A battery dying mid-save leaves a temporary file and never reaches the
    /// rename. The previous state is still whole, and the next save succeeds.
    /// </summary>
    [Fact]
    public async Task ASaveCutShortLeavesThePreviousStateWhole()
    {
        var store = new EncryptedStateFile(StorePath, _key);
        var before = FullState();
        await store.SaveAsync(before);
        await File.WriteAllBytesAsync(StorePath + ".tmp", [0xDE, 0xAD]);

        var loaded = await store.LoadAsync();
        Assert.Equal(before.Brn, loaded!.Brn);

        before.Brn = before.Brn! with { NextAvailable = 200_013 };
        await store.SaveAsync(before);
        Assert.Equal(200_013, (await store.LoadAsync())!.Brn!.NextAvailable);
        Assert.False(File.Exists(StorePath + ".tmp"));
    }

    [Fact]
    public async Task ConcurrentSavesNeverInterleave()
    {
        var store = new EncryptedStateFile(StorePath, _key);
        var states = Enumerable.Range(0, 20).Select(n =>
        {
            var state = FullState();
            state.Brn = state.Brn! with { NextAvailable = 200_000 + n };
            return state;
        }).ToList();

        await Task.WhenAll(states.Select(state => store.SaveAsync(state)));

        var loaded = await store.LoadAsync();
        Assert.InRange(loaded!.Brn!.NextAvailable, 200_000, 200_019);
    }

    [Fact]
    public void AKeyOfTheWrongSizeIsRefused()
        => Assert.Throws<ArgumentException>(() => new EncryptedStateFile(StorePath, new byte[16]));
}

/// <summary>When the store's key may be created, and when its absence is a fault.</summary>
public class StateKeyTests
{
    [Fact]
    public async Task AFirstRunCreatesAndKeepsAKey()
    {
        string? kept = null;

        var key = await StateKey.ResolveAsync(storeExists: false, () => Task.FromResult(kept), value =>
        {
            kept = value;
            return Task.CompletedTask;
        });

        Assert.Equal(EncryptedStateFile.KeySize, key.Length);
        Assert.Equal(Convert.ToBase64String(key), kept);
    }

    [Fact]
    public async Task AKeptKeyIsReturnedNotReplaced()
    {
        var kept = Convert.ToBase64String(EncryptedStateFile.NewKey());

        var key = await StateKey.ResolveAsync(storeExists: true, () => Task.FromResult<string?>(kept),
            _ => throw new InvalidOperationException("must not write"));

        Assert.Equal(kept, Convert.ToBase64String(key));
    }

    /// <summary>
    /// A new key beside an existing store would make the store unreadable and
    /// its first save bury births nobody has synced.
    /// </summary>
    [Fact]
    public async Task ALostKeyBesideAnExistingStoreIsRefusedNotReplaced()
    {
        var written = false;

        await Assert.ThrowsAsync<StateFileUnreadableException>(() => StateKey.ResolveAsync(
            storeExists: true, () => Task.FromResult<string?>(null), _ =>
            {
                written = true;
                return Task.CompletedTask;
            }));

        Assert.False(written);
    }
}
