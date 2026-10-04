using NCBRS.Certificates;
using NCBRS.Client.Certificates;
using NCBRS.Client.Storage;
using NCBRS.Devices;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The core rebuilt from the store and captured back into it, through a real
/// encrypted save and load each time — the restart a tablet goes through every
/// day. What must never happen across one is a BRN handed out twice.
/// </summary>
public sealed class DeviceSessionTests : IDisposable
{
    private static readonly Guid Facility = Guid.Parse("0199c000-0000-7000-8000-00000000f004");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ncbrs-session-" + Guid.NewGuid().ToString("N"));
    private readonly EncryptedStateFile _store;

    public DeviceSessionTests()
        => _store = new EncryptedStateFile(Path.Combine(_directory, "device.state"), EncryptedStateFile.NewKey());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static DeviceState Provisioned(long blockStart = 100_000, long blockEnd = 100_009,
        long? pendingStart = null, long? pendingEnd = null)
        => new()
        {
            Identity = new DeviceIdentity("TABLET-7", Facility, new Uri("https://ncbrs.example/")),
            DevicePrivateKeyPem = DeviceSignature.GenerateKeyPair().PrivateKeyPem,
            Brn = new BrnState(blockStart, blockEnd, blockStart, 0, pendingStart, pendingEnd),
        };

    private static RegisterBirthRequest Birth(string name = "Ayen Deng")
        => new() { ChildFullName = name, DateOfBirth = new DateTime(2026, 9, 27), Sex = Sex.Female };

    /// <summary>A save to disk and a load back, then a fresh session: an app restart.</summary>
    private async Task<(DeviceSession Session, DeviceState State)> RestartAsync(DeviceSession session, DeviceState state)
    {
        await _store.SaveAsync(session.Capture(state));
        session.Dispose();
        var loaded = (await _store.LoadAsync())!;
        return (DeviceSession.Restore(loaded), loaded);
    }

    [Fact]
    public async Task TheCursorSurvivesARestartSoNoNumberIsHandedOutTwice()
    {
        var state = Provisioned();
        var session = DeviceSession.Restore(state);
        var issued = Enumerable.Range(0, 3).Select(_ => session.Facility.RegisterBirth(Birth()).Brn).ToList();

        (session, _) = await RestartAsync(session, state);
        var next = session.Facility.RegisterBirth(Birth()).Brn;

        Assert.Equal(["100000", "100001", "100002"], issued);
        Assert.Equal("100003", next);
        Assert.Equal(4, session.Facility.PendingCount);
        session.Dispose();
    }

    /// <summary>
    /// Rolling over replaces the current block. A store that kept only the
    /// cursor would restore 100011 against the old range and refuse it.
    /// </summary>
    [Fact]
    public async Task ARollOverToTheStagedBlockSurvivesARestart()
    {
        var state = Provisioned(blockStart: 100_000, blockEnd: 100_001, pendingStart: 100_010, pendingEnd: 100_019);
        var session = DeviceSession.Restore(state);
        var issued = Enumerable.Range(0, 3).Select(_ => session.Facility.RegisterBirth(Birth()).Brn).ToList();

        (session, state) = await RestartAsync(session, state);

        Assert.Equal(["100000", "100001", "100010"], issued);
        Assert.Equal(new BrnState(100_010, 100_019, 100_011, 0, null, null), state.Brn);
        Assert.Equal("100011", session.Facility.RegisterBirth(Birth()).Brn);
        session.Dispose();
    }

    /// <summary>
    /// A composed block's office and year survive a restart with its cursor.
    /// Lost, the tablet would carry on handing out the running numbers bare,
    /// as numbers the registry never granted.
    /// </summary>
    [Fact]
    public async Task AComposedBlockSurvivesARestart()
    {
        var state = Provisioned(blockStart: 1, blockEnd: 10);
        state.Brn = new BrnState(1, 10, 1, 0, null, null, "JTH", 2026);
        var session = DeviceSession.Restore(state);
        var first = session.Facility.RegisterBirth(Birth()).Brn;

        (session, state) = await RestartAsync(session, state);

        Assert.Equal(BrnFormat.Compose("JTH", 2026, 1), first);
        Assert.Equal(new BrnState(1, 10, 2, 0, null, null, "JTH", 2026), state.Brn);
        Assert.Equal(BrnFormat.Compose("JTH", 2026, 2), session.Facility.RegisterBirth(Birth()).Brn);
        session.Dispose();
    }

    [Fact]
    public async Task TheDeviceKeySurvivesSoTheTabletStaysTheEnrolledDevice()
    {
        var state = Provisioned();
        var session = DeviceSession.Restore(state);
        var enrolledKey = session.Signer.PublicKeyPem;

        (session, _) = await RestartAsync(session, state);

        Assert.Equal(enrolledKey, session.Signer.PublicKeyPem);
        session.Dispose();
    }

    /// <summary>
    /// The upload in flight is resent as the same bytes under the same
    /// transaction id — so it must come back from the store exactly, and its
    /// signature must still verify.
    /// </summary>
    [Fact]
    public async Task TheUploadInFlightSurvivesByteForByte()
    {
        var state = Provisioned();
        var session = DeviceSession.Restore(state);
        session.Facility.RegisterBirth(Birth());
        var upload = session.Facility.BuildSignedUpload();
        session.Sync.InFlight = upload;
        var publicKey = session.Signer.PublicKeyPem;

        (session, _) = await RestartAsync(session, state);

        var restored = session.Sync.InFlight;
        Assert.NotNull(restored);
        Assert.Equal(upload.Body, restored.Body);
        Assert.Equal(upload.TransactionId, restored.TransactionId);
        Assert.True(DeviceSignature.Verify(publicKey, restored.Body, restored.Signature).Valid);
        session.Dispose();
    }

    [Fact]
    public async Task ASettledBirthStaysSettledAcrossARestart()
    {
        var state = Provisioned();
        var session = DeviceSession.Restore(state);
        var kept = session.Facility.RegisterBirth(Birth("Ayen Deng"));
        var settled = session.Facility.RegisterBirth(Birth("Bol Kenyi"));
        session.Facility.Settle(new SyncBatchResponse(Guid.NewGuid(), SyncBatchStatus.Reconciled, 2, 1, 0, 1,
        [
            new SyncRecordOutcome(settled.Brn, SyncRecordStatus.Registered),
            new SyncRecordOutcome(kept.Brn, SyncRecordStatus.Rejected),
        ]));

        (session, state) = await RestartAsync(session, state);

        var remaining = Assert.Single(state.Outbox);
        Assert.Equal(kept.Brn, remaining.Birth.Brn);
        Assert.Equal(1, session.Facility.PendingCount);
        session.Dispose();
    }

    [Fact]
    public async Task TheVerificationBundleSurvivesAndStillVerifies()
    {
        var fetched = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);
        var state = Provisioned();
        var session = DeviceSession.Restore(state);
        session.Sync.Bundle = CachedVerificationBundle.From(
            [EncryptedStateFileTests.SelfSignedKey()], [EncryptedStateFileTests.ListExpiring(fetched.AddDays(7))], fetched);

        (session, _) = await RestartAsync(session, state);

        Assert.True(session.Sync.Bundle.HasBundle);
        Assert.Equal(fetched, session.Sync.Bundle.FetchedAtUtc);
        Assert.Equal(fetched.AddDays(7), session.Sync.Bundle.ExpiresAtUtc);
        Assert.False(session.Sync.Bundle.RefreshDue(fetched.AddDays(1)));
        Assert.Equal(OfflineVerdict.NotGenuine, session.Sync.Bundle.Verify("not.a.real.signature", fetched).Verdict);
        session.Dispose();
    }

    [Fact]
    public async Task ASessionNeverFetchedCapturesNoBundle()
    {
        var state = Provisioned();
        var session = DeviceSession.Restore(state);

        (session, state) = await RestartAsync(session, state);

        Assert.Null(state.Bundle);
        Assert.False(session.Sync.Bundle.HasBundle);
        session.Dispose();
    }

    [Fact]
    public void CaptureLeavesWhatTheSessionDoesNotOwnAlone()
    {
        var state = Provisioned();
        state.OfflineToken = "offline-token";
        state.Attempts = new PinAttempts(3, null);
        using var session = DeviceSession.Restore(state);

        session.Capture(state);

        Assert.Equal("offline-token", state.OfflineToken);
        Assert.Equal(3, state.Attempts.FailedAttempts);
    }

    [Fact]
    public void ATabletStillBeingHandedOverCannotBeRestored()
    {
        var state = new DeviceState
        {
            Identity = new DeviceIdentity("TABLET-7", Facility, new Uri("https://ncbrs.example/")),
            DevicePrivateKeyPem = DeviceSignature.GenerateKeyPair().PrivateKeyPem,
        };

        Assert.False(DeviceSession.CanRestore(state));
        Assert.Throws<InvalidOperationException>(() => DeviceSession.Restore(state));
    }
}
