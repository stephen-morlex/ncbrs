using NCBRS.Client.Storage;
using NCBRS.Client.Sync;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The tablet's short history of births the registry confirmed: what the
/// Records list shows, kept 30 days and then forgotten, so a lost tablet holds
/// at most a month of names.
/// </summary>
public sealed class RecentBirthsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Facility = Guid.Parse("0199c000-0000-7000-8000-0000000f0003");
    private const string Device = "TAB-0A1B2C3D4E5F";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ncbrs-recent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RegisterBirthRequest Birth(string given, string surname, DateTime? capturedAt = null) => new()
    {
        ChildGivenNames = given,
        ChildSurname = surname,
        PlaceOfBirthKind = PlaceOfBirthKind.ThisFacility,
        DateOfBirth = Now.Date.AddDays(-1),
        Sex = Sex.Female,
        RegisteredAtUtc = capturedAt ?? Now,
    };

    private static SyncBatchResponse Answer(params (string Brn, SyncRecordStatus Status)[] outcomes)
        => new(Guid.NewGuid(), SyncBatchStatus.Reconciled, outcomes.Length,
            outcomes.Count(o => o.Status == SyncRecordStatus.Registered), 0,
            outcomes.Count(o => o.Status == SyncRecordStatus.Rejected),
            [.. outcomes.Select(o => new SyncRecordOutcome(o.Brn, o.Status,
                o.Status == SyncRecordStatus.Rejected ? [new ApiError("sex", "sex is required.")] : null))]);

    private static FacilityClient Client()
        => new(Device, Facility, new Brn.DeviceBrnAllocator(Device, 100_000, 100_099), new SyncOutbox(Device, Facility),
            DeviceSigner.Generate());

    [Fact]
    public void AConfirmedBirthIsKeptWithWhatTheRecordsListShows()
    {
        var client = Client();
        var confirmed = client.RegisterBirth(Birth("Ayen", "Garang")).Brn;
        var refused = client.RegisterBirth(Birth("Bol", "Kenyi")).Brn;

        client.Settle(Answer((confirmed, SyncRecordStatus.Registered), (refused, SyncRecordStatus.Rejected)), Now.AddHours(2));

        // The refused birth is still the outbox's, held for correction: not history.
        var birth = Assert.Single(client.Recent.All);
        Assert.Equal(confirmed, birth.Brn);
        Assert.Equal("Ayen Garang", birth.ChildName);
        Assert.Equal(Now.Date.AddDays(-1), birth.DateOfBirth);
        Assert.Equal(Sex.Female, birth.Sex);
        Assert.Equal(Now, birth.RegisteredAtUtc);
        Assert.Equal(Now.AddHours(2), birth.ConfirmedAtUtc);
        Assert.False(birth.CertificatePrinted);
        Assert.Null(birth.ProvisionalBrn);
    }

    /// <summary>A re-sent batch is answered "already held": the birth is settled, but listed once.</summary>
    [Fact]
    public void ABirthSettledTwiceIsListedOnce()
    {
        var client = Client();
        var brn = client.RegisterBirth(Birth("Ayen", "Garang")).Brn;

        client.Settle(Answer((brn, SyncRecordStatus.Registered)), Now);
        client.Settle(Answer((brn, SyncRecordStatus.Duplicate)), Now.AddMinutes(5));

        Assert.Equal(brn, Assert.Single(client.Recent.All).Brn);
    }

    /// <summary>The family may still hold the slip with the provisional number, so it stays beside the real one.</summary>
    [Fact]
    public void AProvisionalBirthIsKeptUnderTheNumberTheRegistryAssigned()
    {
        var recent = new RecentBirths();
        var record = new SyncBirthRecord { Birth = Birth("Ayen", "Garang") with { Brn = "PROV-TAB-0A1B2C3D4E5F-1" } };
        var outcome = new SyncRecordOutcome("PROV-TAB-0A1B2C3D4E5F-1", SyncRecordStatus.Registered, AssignedBrn: "SS-JTH-2026-000015-B");

        recent.Record(new OutboxSettlement([outcome], [], 0, [record]), Now);

        var birth = Assert.Single(recent.All);
        Assert.Equal("SS-JTH-2026-000015-B", birth.Brn);
        Assert.Equal("PROV-TAB-0A1B2C3D4E5F-1", birth.ProvisionalBrn);
    }

    [Fact]
    public void ABirthIsForgottenThirtyDaysAfterItWasRegistered()
    {
        var recent = new RecentBirths([
            new RecentBirth("SS-JTH-2026-000001-1", "Ayen Garang", Now.Date, Sex.Female, Now, Now),
        ]);

        recent.Forget(Now.AddDays(30));
        Assert.Single(recent.All);

        recent.Forget(Now.AddDays(30).AddMinutes(1));
        Assert.Empty(recent.All);
    }

    /// <summary>
    /// Measured from registration, not confirmation: a birth that sat three
    /// weeks offline and was confirmed today has had its month already.
    /// </summary>
    [Fact]
    public void TheMonthRunsFromRegistrationNotFromWhenTheRegistryConfirmed()
    {
        var client = Client();
        var brn = client.RegisterBirth(Birth("Ayen", "Garang", capturedAt: Now.AddDays(-25))).Brn;

        client.Settle(Answer((brn, SyncRecordStatus.Registered)), Now);
        Assert.Single(client.Recent.All);

        client.Recent.Forget(Now.AddDays(6));
        Assert.Empty(client.Recent.All);
    }

    [Fact]
    public void APrintedCertificateIsNotedAgainstTheBirth()
    {
        var recent = new RecentBirths([
            new RecentBirth("SS-JTH-2026-000001-1", "Ayen Garang", Now.Date, Sex.Female, Now, Now),
        ]);

        recent.MarkPrinted(" ss-jth-2026-000001-1 ");

        Assert.True(Assert.Single(recent.All).CertificatePrinted);
    }

    /// <summary>Kept in the encrypted store with the rest of the tablet's state, and forgotten on restore once old.</summary>
    [Fact]
    public async Task TheHistorySurvivesARestartAndIsForgottenWhenOld()
    {
        var store = new EncryptedStateFile(Path.Combine(_directory, "device.state"), EncryptedStateFile.NewKey());
        var state = new DeviceState
        {
            Identity = new DeviceIdentity(Device, Facility, new Uri("https://registry.ncbrs.ss/")),
            DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem(),
            Brn = new BrnState(100_000, 100_099, 100_000, 0, null, null),
        };
        var session = DeviceSession.Restore(state, nowUtc: Now);
        var brn = session.Facility.RegisterBirth(Birth("Ayen", "Garang")).Brn;
        session.Facility.Settle(Answer((brn, SyncRecordStatus.Registered)), Now);
        await store.SaveAsync(session.Capture(state));
        session.Dispose();

        using (var restored = DeviceSession.Restore((await store.LoadAsync())!, nowUtc: Now.AddDays(1)))
        {
            Assert.Equal(brn, Assert.Single(restored.Facility.Recent.All).Brn);
        }

        using var monthLater = DeviceSession.Restore((await store.LoadAsync())!, nowUtc: Now.AddDays(31));
        Assert.Empty(monthLater.Facility.Recent.All);
        Assert.Empty(monthLater.Capture(new DeviceState()).Recent);
    }
}
