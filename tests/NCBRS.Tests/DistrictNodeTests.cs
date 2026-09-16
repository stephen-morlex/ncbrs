using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.District.Data;
using NCBRS.District.Models;
using NCBRS.District.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the district node's store-and-forward behaviour (draft 6.2, 7.2).
///
/// The node exists for one situation: the national tier is unreachable and a
/// village post still has to sync. What has to hold through that is that
/// nothing is lost, nothing is registered twice when the link returns, and a
/// batch the centre has not seen is never reported as though it had.
/// </summary>
public class DistrictNodeTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DistrictDbContext> _options;

    public DistrictNodeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DistrictDbContext>().UseSqlite(_connection).Options;

        using var db = new DistrictDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private DistrictDbContext NewDb() => new(_options);

    private static ForwardedBatch Batch(Guid? transactionId = null)
        => new()
        {
            TransactionId = transactionId ?? Guid.CreateVersion7(),
            DeviceId = "TABLET-07",
            FacilityId = FacilityId,
            RecordCount = 3,
            Payload = """{"meta":{"transactionId":"x"},"data":{"records":[]}}"""
        };

    // --- how an attempt is recorded ----------------------------------------

    [Fact]
    public void AnAcceptedForward_StoresTheCentresAnswer()
    {
        var batch = Batch();

        BatchForwarder.Record(batch, new CentralForwardResult(true, 200, """{"data":{"registered":3}}"""));

        Assert.Equal(ForwardedBatchStatus.Forwarded, batch.Status);
        Assert.NotNull(batch.ForwardedAtUtc);
        Assert.Contains("registered", batch.CentralResponse);
        Assert.Null(batch.NextAttemptAtUtc);
        Assert.Null(batch.LastError);
    }

    /// <summary>
    /// The distinction the whole node turns on: "the centre said no" is not
    /// the same as "the centre did not answer", and treating them alike would
    /// mean discarding births whenever a link drops.
    /// </summary>
    [Fact]
    public void AnUnreachableCentre_LeavesTheBatchQueued()
    {
        var batch = Batch();

        BatchForwarder.Record(batch, new CentralForwardResult(false, Error: "connection refused"));

        Assert.Equal(ForwardedBatchStatus.Queued, batch.Status);
        Assert.Equal(1, batch.Attempts);
        Assert.NotNull(batch.NextAttemptAtUtc);
        Assert.Contains("connection refused", batch.LastError);
    }

    /// <summary>
    /// A 4xx will not change on retry, so it stops -- but is kept, because the
    /// district has to be able to tell the facility what was refused.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    public void APermanentRefusal_StopsRetryingButIsRetained(int status)
    {
        var batch = Batch();

        BatchForwarder.Record(batch, new CentralForwardResult(true, status, """{"errors":[]}"""));

        Assert.Equal(ForwardedBatchStatus.Rejected, batch.Status);
        Assert.Null(batch.NextAttemptAtUtc);
        Assert.Equal(status, batch.CentralStatusCode);
        Assert.NotNull(batch.CentralResponse);
    }

    /// <summary>
    /// A 5xx is the centre having a bad moment, and 408/429 are explicit
    /// invitations to come back -- none is a reason to give up on a birth.
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(408)]
    [InlineData(429)]
    public void ATransientRefusal_StaysQueued(int status)
    {
        var batch = Batch();

        BatchForwarder.Record(batch, new CentralForwardResult(true, status, "upstream error"));

        Assert.Equal(ForwardedBatchStatus.Queued, batch.Status);
        Assert.NotNull(batch.NextAttemptAtUtc);
    }

    /// <summary>
    /// A district link down all night must not come back to a node hammering
    /// the centre, so the wait grows with each failure.
    /// </summary>
    [Fact]
    public void RepeatedFailures_BackOff()
    {
        var batch = Batch();
        var waits = new List<TimeSpan>();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var before = DateTime.UtcNow;
            BatchForwarder.Record(batch, new CentralForwardResult(false, Error: "down"));
            waits.Add(batch.NextAttemptAtUtc!.Value - before);
        }

        Assert.True(waits[1] > waits[0], "second wait should exceed the first");
        Assert.True(waits[2] > waits[1], "third wait should exceed the second");
        Assert.True(waits[3] <= TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(5)),
            "backoff must stay capped");
    }

    /// <summary>
    /// A batch that eventually gets through must not carry the wreckage of
    /// the attempts that failed.
    /// </summary>
    [Fact]
    public void ABatchThatSucceedsAfterFailing_ClearsItsError()
    {
        var batch = Batch();

        BatchForwarder.Record(batch, new CentralForwardResult(false, Error: "down"));
        BatchForwarder.Record(batch, new CentralForwardResult(true, 200, "{}"));

        Assert.Equal(ForwardedBatchStatus.Forwarded, batch.Status);
        Assert.Null(batch.LastError);
        Assert.Null(batch.NextAttemptAtUtc);
        Assert.Equal(2, batch.Attempts);
    }

    // --- the queue ----------------------------------------------------------

    /// <summary>
    /// End-to-end idempotency (the plan's D2). The transaction id is carried
    /// through unchanged, so the centre recognises a re-forwarded batch as
    /// the same submission -- and the node refuses to hold it twice for the
    /// same reason.
    /// </summary>
    [Fact]
    public async Task TheSameSubmissionTwice_IsHeldOnce()
    {
        var transactionId = Guid.CreateVersion7();

        await using var db = NewDb();
        db.ForwardedBatches.Add(Batch(transactionId));
        await db.SaveChangesAsync();

        db.ForwardedBatches.Add(Batch(transactionId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// A district reconnecting after an outage delivers its backlog in the
    /// order it was taken, so the register reflects the sequence events
    /// actually happened in.
    /// </summary>
    [Fact]
    public async Task TheQueueDrainsOldestFirst()
    {
        await using var db = NewDb();

        var older = Batch();
        older.ReceivedAtUtc = DateTime.UtcNow.AddHours(-3);

        var newer = Batch();
        newer.ReceivedAtUtc = DateTime.UtcNow.AddHours(-1);

        db.ForwardedBatches.AddRange(newer, older);
        await db.SaveChangesAsync();

        var due = await db.ForwardedBatches
            .Where(batch => batch.Status == ForwardedBatchStatus.Queued)
            .OrderBy(batch => batch.ReceivedAtUtc)
            .ToListAsync();

        Assert.Equal(older.TransactionId, due[0].TransactionId);
    }

    /// <summary>
    /// A batch waiting out its backoff is not due yet, so a node with one bad
    /// batch does not spin on it while others wait.
    /// </summary>
    [Fact]
    public async Task ABatchInsideItsBackoff_IsNotDue()
    {
        await using var db = NewDb();

        var waiting = Batch();
        waiting.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(10);

        var ready = Batch();

        db.ForwardedBatches.AddRange(waiting, ready);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var due = await db.ForwardedBatches
            .Where(batch => batch.Status == ForwardedBatchStatus.Queued
                            && (batch.NextAttemptAtUtc == null || batch.NextAttemptAtUtc <= now))
            .ToListAsync();

        Assert.Equal(ready.TransactionId, Assert.Single(due).TransactionId);
    }

    /// <summary>
    /// Once forwarded, a batch leaves the queue for good -- the node must not
    /// re-push work the centre has already accepted.
    /// </summary>
    [Fact]
    public async Task AForwardedBatch_IsNoLongerQueued()
    {
        await using var db = NewDb();

        var batch = Batch();
        BatchForwarder.Record(batch, new CentralForwardResult(true, 200, "{}"));

        db.ForwardedBatches.Add(batch);
        await db.SaveChangesAsync();

        Assert.Empty(await db.ForwardedBatches
            .Where(entry => entry.Status == ForwardedBatchStatus.Queued)
            .ToListAsync());
    }

    /// <summary>
    /// The node keeps the centre's per-record answer so a device told
    /// "queued" can come back later and learn which of its records
    /// registered, which were duplicates and which failed.
    /// </summary>
    [Fact]
    public async Task TheCentresPerRecordAnswer_SurvivesForLaterCollection()
    {
        const string central =
            """{"data":{"registered":2,"duplicates":1,"records":[{"brn":"100001","status":"Registered"}]}}""";

        await using var db = NewDb();

        var batch = Batch();
        BatchForwarder.Record(batch, new CentralForwardResult(true, 200, central));

        db.ForwardedBatches.Add(batch);
        await db.SaveChangesAsync();

        await using var verify = NewDb();
        var stored = await verify.ForwardedBatches.SingleAsync();

        Assert.Contains("\"brn\":\"100001\"", stored.CentralResponse);
    }

    /// <summary>
    /// The payload is replayed verbatim. A node that re-serialised a batch
    /// would be a second place registration logic could drift from the
    /// centre's.
    /// </summary>
    [Fact]
    public async Task ThePayload_IsHeldByteForByte()
    {
        const string payload =
            """{"meta":{"transactionId":"6f1c1f2e-0000-4000-8000-000000000001"},"data":{"deviceId":"TABLET-07"}}""";

        await using var db = NewDb();

        var batch = Batch();
        batch.Payload = payload;

        db.ForwardedBatches.Add(batch);
        await db.SaveChangesAsync();

        await using var verify = NewDb();
        Assert.Equal(payload, (await verify.ForwardedBatches.SingleAsync()).Payload);
    }
}
