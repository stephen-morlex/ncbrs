using NCBRS.Client.Sync;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The device's local outbox (WS-B6): stage offline, upload in one batch, and
/// on the centre's answer settle exactly what was accepted while keeping the
/// rejected records queued.
/// </summary>
public class SyncOutboxTests
{
    private static readonly Guid Facility = Guid.Parse("0199c000-0000-7000-8000-00000000f001");

    private static SyncBirthRecord Entry(string brn)
        => new() { Birth = new RegisterBirthRequest { Brn = brn, FacilityId = Facility, DeviceId = "TABLET-1" } };

    private static SyncRecordOutcome Outcome(string brn, SyncRecordStatus status, string? assignedBrn = null)
        => new(brn, status, AssignedBrn: assignedBrn);

    private static SyncBatchResponse Response(params SyncRecordOutcome[] outcomes)
        => new(Guid.NewGuid(), SyncBatchStatus.Reconciled, outcomes.Length,
            outcomes.Count(o => o.Status == SyncRecordStatus.Registered),
            outcomes.Count(o => o.Status == SyncRecordStatus.Duplicate),
            outcomes.Count(o => o.Status == SyncRecordStatus.Rejected),
            outcomes);

    [Fact]
    public void BuildsABatchOfEverythingQueued()
    {
        var outbox = new SyncOutbox("TABLET-1", Facility);
        outbox.Enqueue(Entry("100000"));
        outbox.Enqueue(Entry("100001"));

        var batch = outbox.BuildBatch();

        Assert.Equal("TABLET-1", batch.DeviceId);
        Assert.Equal(Facility, batch.FacilityId);
        Assert.Equal(2, batch.Records.Count);
    }

    [Fact]
    public void EnqueueIsIdempotentByBrn()
    {
        var outbox = new SyncOutbox("TABLET-1", Facility);
        outbox.Enqueue(Entry("100000"));
        outbox.Enqueue(Entry("100000"));

        Assert.Equal(1, outbox.Count);
    }

    [Fact]
    public void APartiallyRejectedBatchLeavesExactlyTheRejectedRecordsQueued()
    {
        var outbox = new SyncOutbox("TABLET-1", Facility);
        outbox.Enqueue(Entry("100000"));
        outbox.Enqueue(Entry("100001"));
        outbox.Enqueue(Entry("100002"));

        var settlement = outbox.Settle(Response(
            Outcome("100000", SyncRecordStatus.Registered),
            Outcome("100001", SyncRecordStatus.Duplicate),
            Outcome("100002", SyncRecordStatus.Rejected)));

        Assert.Equal(2, settlement.Settled.Count);
        Assert.Single(settlement.Rejected);
        Assert.Equal(1, settlement.RemainingCount);
        Assert.Equal("100002", Assert.Single(outbox.Pending).Birth.Brn);
    }

    [Fact]
    public void LeavesRecordsTheResponseNeverMentionedQueued()
    {
        // Staged after the batch was built, so the centre's answer says nothing
        // about it — it must survive to the next upload rather than vanish.
        var outbox = new SyncOutbox("TABLET-1", Facility);
        outbox.Enqueue(Entry("100000"));
        outbox.Enqueue(Entry("100001"));

        outbox.Settle(Response(Outcome("100000", SyncRecordStatus.Registered)));

        Assert.Equal("100001", Assert.Single(outbox.Pending).Birth.Brn);
    }

    [Fact]
    public void SurfacesThePermanentBrnAssignedToAProvisionalRecord()
    {
        var outbox = new SyncOutbox("TABLET-1", Facility);
        outbox.Enqueue(Entry("PROV-TABLET-1-1"));

        var settlement = outbox.Settle(Response(
            Outcome("PROV-TABLET-1-1", SyncRecordStatus.Registered, assignedBrn: "100050")));

        Assert.Equal(0, outbox.Count);
        var settled = Assert.Single(settlement.Settled);
        Assert.Equal("100050", settled.AssignedBrn);
    }
}
