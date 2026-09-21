using System.Text.Json;
using NCBRS.Client;
using NCBRS.Client.Brn;
using NCBRS.Client.Sync;
using NCBRS.Devices;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The offline registration workflow (FacilityClient): the tested client-core
/// pieces composed into the acts a registrar performs — allocate, stage, sign,
/// transfer, settle.
/// </summary>
public class FacilityClientTests
{
    private const string Device = "TABLET-1";
    private static readonly Guid Facility = Guid.Parse("0199c000-0000-7000-8000-00000000f001");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static (FacilityClient Client, DeviceSigner Signer) Build(long blockStart = 100_000, long blockEnd = 100_009)
    {
        var signer = DeviceSigner.Generate();
        var client = new FacilityClient(
            Device, Facility,
            new DeviceBrnAllocator(Device, blockStart, blockEnd),
            new SyncOutbox(Device, Facility),
            signer,
            lowBlockThreshold: 2);
        return (client, signer);
    }

    private static RegisterBirthRequest Birth() => new() { ChildFullName = "Ayen Deng", Sex = Sex.Female };

    [Fact]
    public void RegisterBirthAllocatesStagesAndStampsTheFacilityAndDevice()
    {
        var (client, signer) = Build();

        var draft = client.RegisterBirth(Birth());

        Assert.Equal("100000", draft.Brn);
        Assert.False(draft.IsProvisional);
        Assert.Equal(1, client.PendingCount);

        // The number, facility and device are the client's to set.
        var batch = JsonSerializer.Deserialize<SyncBatchRequest>(client.BuildSignedUpload().Body, Json)!;
        var record = Assert.Single(batch.Records);
        Assert.Equal("100000", record.Birth.Brn);
        Assert.Equal(Facility, record.Birth.FacilityId);
        Assert.Equal(Device, record.Birth.DeviceId);
        _ = signer;
    }

    [Fact]
    public void FallsBackToAProvisionalNumberAndWarnsWhenLow()
    {
        var (client, _) = Build(blockStart: 1, blockEnd: 2);

        var first = client.RegisterBirth(Birth());   // 1 left after -> low (threshold 2)
        Assert.False(first.IsProvisional);
        Assert.True(first.BlockLow);

        client.RegisterBirth(Birth());                // block now exhausted
        var provisional = client.RegisterBirth(Birth());
        Assert.True(provisional.IsProvisional);
        Assert.StartsWith("PROV-", provisional.Brn);
    }

    [Fact]
    public void TopsUpTheBlockAtAConnectivityWindowAndStaysOnRealNumbers()
    {
        var (client, _) = Build(blockStart: 1, blockEnd: 2);

        client.RegisterBirth(Birth());                 // 1 left -> low
        Assert.True(client.NeedsMoreNumbers);

        client.GrantNextBlock(100, 101);               // top-up fetched while online
        Assert.False(client.NeedsMoreNumbers);         // in hand; stop asking

        client.RegisterBirth(Birth());                 // drains the first block
        var rolled = client.RegisterBirth(Birth());    // rolls over to the staged block
        Assert.False(rolled.IsProvisional);
        Assert.Equal("100", rolled.Brn);
    }

    [Fact]
    public void TheSignedUploadVerifiesWithTheCentresVerifier()
    {
        var (client, signer) = Build();
        client.RegisterBirth(Birth());

        var upload = client.BuildSignedUpload();

        Assert.Equal("X-NCBRS-Device-Signature", upload.HeaderName);
        Assert.True(DeviceSignature.Verify(signer.PublicKeyPem, upload.Body, upload.Signature).Valid);
    }

    [Fact]
    public void TheTransferFileOpensToTheSameBatch()
    {
        var (client, signer) = Build();
        client.RegisterBirth(Birth());

        var file = client.BuildTransferFile();
        var opened = OfflineTransferFile.Open(file, signer.PublicKeyPem);

        Assert.True(opened.Accepted);
        var batch = JsonSerializer.Deserialize<SyncBatchRequest>(opened.Body!, Json)!;
        Assert.Equal("100000", Assert.Single(batch.Records).Birth.Brn);
    }

    [Fact]
    public void SettlingLeavesRejectedRecordsQueued()
    {
        var (client, _) = Build();
        var a = client.RegisterBirth(Birth());
        var b = client.RegisterBirth(Birth());

        var response = new SyncBatchResponse(Guid.NewGuid(), SyncBatchStatus.Reconciled, 2, 1, 0, 1,
        [
            new SyncRecordOutcome(a.Brn, SyncRecordStatus.Registered),
            new SyncRecordOutcome(b.Brn, SyncRecordStatus.Rejected),
        ]);

        var settlement = client.Settle(response);

        Assert.Single(settlement.Settled);
        Assert.Single(settlement.Rejected);
        Assert.Equal(1, client.PendingCount);
    }
}
