using System.Text.Json;
using NCBRS.Client.Network;
using NCBRS.Client.Storage;
using NCBRS.Client.Sync;
using NCBRS.Models;
using Xunit;
using static NCBRS.Client.Tests.CentralClientTests;

namespace NCBRS.Client.Tests;

/// <summary>
/// A birth the registry refuses. Before this it stayed in the outbox
/// unchanged, was sent again every window and refused again every window —
/// forever, with the registrar never told why and no way to put it right.
/// Now it is held with the registry's reasons, corrected on the tablet, and
/// sent again once, as corrected.
/// </summary>
public sealed class RefusedBirthTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Facility = Guid.Parse("0199c000-0000-7000-8000-0000000f0003");
    private const string Device = "TAB-0A1B2C3D4E5F";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ncbrs-refused-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RegisterBirthRequest Birth(string name, DateTime? capturedAt = null) => new()
    {
        ChildFullName = name,
        DateOfBirth = Now.Date.AddDays(-2),
        Sex = Sex.Female,
        RegisteredAtUtc = capturedAt ?? Now,
    };

    private static SyncBatchResponse Answer(params (string Brn, SyncRecordStatus Status)[] outcomes)
        => new(Guid.NewGuid(), SyncBatchStatus.Reconciled, outcomes.Length,
            outcomes.Count(o => o.Status == SyncRecordStatus.Registered), 0,
            outcomes.Count(o => o.Status == SyncRecordStatus.Rejected),
            [.. outcomes.Select(o => new SyncRecordOutcome(o.Brn, o.Status,
                o.Status == SyncRecordStatus.Rejected ? [new ApiError("birthWeightGrams", "birthWeightGrams must be between 200 and 9999 when supplied.")] : null))]);

    private static (FacilityClient Client, SyncOutbox Outbox) Client()
    {
        var outbox = new SyncOutbox(Device, Facility);
        return (new FacilityClient(Device, Facility, new Brn.DeviceBrnAllocator(Device, 100_000, 100_099), outbox,
            DeviceSigner.Generate()), outbox);
    }

    [Fact]
    public void ARefusedBirthIsHeldWithTheRegistrysReasonsAndNotSentAgain()
    {
        var (client, outbox) = Client();
        var refused = client.RegisterBirth(Birth("Ayen Deng")).Brn;
        var accepted = client.RegisterBirth(Birth("Bol Kenyi")).Brn;

        client.Settle(Answer((refused, SyncRecordStatus.Rejected), (accepted, SyncRecordStatus.Registered)));
        var later = client.RegisterBirth(Birth("Achol Garang")).Brn;

        var (record, reasons) = Assert.Single(client.Refused);
        Assert.Equal(refused, record.Birth.Brn);
        Assert.Contains("birthWeightGrams", reasons.Select(reason => reason.Field));
        Assert.Equal(2, client.PendingCount);
        Assert.Equal(1, client.SendableCount);
        Assert.Equal([later], outbox.BuildBatch().Records.Select(r => r.Birth.Brn));
    }

    /// <summary>
    /// The number is on the family's slip, and the window is measured to the
    /// capture: a correction changes neither, whatever it carries.
    /// </summary>
    [Fact]
    public void ACorrectionKeepsTheNumberAndTheCaptureTimeAndIsSentOnce()
    {
        var (client, outbox) = Client();
        var captured = Now.AddDays(-5);
        var brn = client.RegisterBirth(Birth("Ayen Deng", captured) with { BirthWeightGrams = 150 }).Brn;
        client.Settle(Answer((brn, SyncRecordStatus.Rejected)));

        client.Correct(brn, Birth("Ayen Deng") with
        {
            BirthWeightGrams = 1500,
            Brn = "999999",
            RegisteredAtUtc = Now,
            FacilityId = Guid.NewGuid(),
            DeviceId = "SOMEONE-ELSE",
        });

        var sent = Assert.Single(outbox.BuildBatch().Records).Birth;
        Assert.Equal(1500, sent.BirthWeightGrams);
        Assert.Equal(brn, sent.Brn);
        Assert.Equal(captured, sent.RegisteredAtUtc);
        Assert.Equal(Facility, sent.FacilityId);
        Assert.Equal(Device, sent.DeviceId);
        Assert.Empty(client.Refused);
    }

    [Fact]
    public void OnlyARefusedBirthCanBeCorrectedHere()
    {
        var (client, _) = Client();
        var brn = client.RegisterBirth(Birth("Ayen Deng")).Brn;

        Assert.Throws<InvalidOperationException>(() => client.Correct(brn, Birth("Ayen Deng")));
        Assert.Throws<InvalidOperationException>(() => client.Correct("100050", Birth("Nobody")));
    }

    [Fact]
    public void ACorrectedBirthTheRegistryAcceptsIsSettled()
    {
        var (client, _) = Client();
        var brn = client.RegisterBirth(Birth("Ayen Deng")).Brn;
        client.Settle(Answer((brn, SyncRecordStatus.Rejected)));
        client.Correct(brn, Birth("Ayen Deng") with { BirthWeightGrams = 1500 });

        client.Settle(Answer((brn, SyncRecordStatus.Registered)));

        Assert.Equal(0, client.PendingCount);
        Assert.Empty(client.Refused);
    }

    /// <summary>Lost with a restart, a refusal would put the birth back into every upload.</summary>
    [Fact]
    public async Task AHeldBirthStaysHeldAcrossARestart()
    {
        var store = new EncryptedStateFile(Path.Combine(_directory, "device.state"), EncryptedStateFile.NewKey());
        var state = new DeviceState
        {
            Identity = new DeviceIdentity(Device, Facility, new Uri("https://registry.ncbrs.ss/")),
            DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem(),
            Brn = new BrnState(100_000, 100_099, 100_000, 0, null, null),
        };
        var session = DeviceSession.Restore(state);
        var brn = session.Facility.RegisterBirth(Birth("Ayen Deng")).Brn;
        session.Facility.Settle(Answer((brn, SyncRecordStatus.Rejected)));
        await store.SaveAsync(session.Capture(state));
        session.Dispose();

        using var restored = DeviceSession.Restore((await store.LoadAsync())!);

        var (record, reasons) = Assert.Single(restored.Facility.Refused);
        Assert.Equal(brn, record.Birth.Brn);
        Assert.Single(reasons);
        Assert.Equal(0, restored.Facility.SendableCount);
    }

    /// <summary>
    /// Found writing the test below: an offline bundle answered without its
    /// keys crashed the whole window, births already uploaded in it included.
    /// </summary>
    [Fact]
    public async Task AnIncompleteBundleIsReportedNotTakenOrCrashedOn()
    {
        var (client, _) = Client();
        client.RegisterBirth(Birth("Ayen Deng"));
        var network = new FakeNetwork
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/api/Sync/batches", StringComparison.Ordinal)
                ? FakeNetwork.Envelope(JsonSerializer.Serialize(Answer((client.Refused.Count == 0 ? "100000" : "", SyncRecordStatus.Registered)), ClientJson.Options))
                : FakeNetwork.Envelope("{}"),
        };
        var state = new ClientSyncState();

        var report = await new ConnectivityWindow(client, CentreClient(network), DeviceSigner.Generate(), (_, _) => Task.CompletedTask)
            .RunAsync(state, Now);

        Assert.Equal(CentralOutcome.Succeeded, report.Upload);
        Assert.False(report.BundleRefreshed);
        Assert.False(state.Bundle.HasBundle);
        Assert.Contains(report.Problems, problem => problem.Contains("incomplete"));
    }

    /// <summary>The loop this closes: the next window sends nothing for a birth still waiting to be corrected.</summary>
    [Fact]
    public async Task TheNextWindowDoesNotSendARefusedBirthAgain()
    {
        var (client, _) = Client();
        var signer = DeviceSigner.Generate();
        var brn = client.RegisterBirth(Birth("Ayen Deng")).Brn;
        var uploads = 0;
        var network = new FakeNetwork
        {
            Respond = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/api/Sync/batches", StringComparison.Ordinal))
                {
                    uploads++;
                    return FakeNetwork.Envelope(JsonSerializer.Serialize(Answer((brn, SyncRecordStatus.Rejected)), ClientJson.Options));
                }

                return FakeNetwork.Envelope("{}");
            },
        };
        var window = new ConnectivityWindow(client, CentreClient(network), signer, (_, _) => Task.CompletedTask);
        var state = new ClientSyncState();

        var first = await window.RunAsync(state, Now);
        var second = await window.RunAsync(state, Now);

        Assert.Equal(1, uploads);
        Assert.Single(first.Settlements.SelectMany(settlement => settlement.Rejected));
        Assert.Null(second.Upload);
        Assert.Single(client.Refused);
    }
}
