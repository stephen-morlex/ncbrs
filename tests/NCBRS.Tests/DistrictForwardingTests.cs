using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Devices;
using NCBRS.District.Controllers;
using NCBRS.District.Data;
using NCBRS.District.Models;
using NCBRS.District.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The district node's forwarding path, end to end against a fake centre:
/// the three defects reproduced in plan §17 (11a–11c).
///
/// <list type="bullet">
/// <item>11c — the device's signature was never forwarded, so with signing
/// enforced (the default) every forwarded batch was refused.</item>
/// <item>11b — a new batch was forwarded twice at once, inline and by the
/// poller, and the centre's "already in progress" answer to the second copy
/// marked a registered batch as rejected.</item>
/// <item>11a — one flat timeout for every batch: a batch that could not fit in
/// it was cancelled, rolled back at the centre, and retried identically
/// forever while reporting an outage.</item>
/// </list>
///
/// The existing <see cref="DistrictNodeTests"/> exercise <c>Record</c> and the
/// queue queries directly, which is why none of these was caught: all three
/// lived in the controller, the HTTP client and the drain loop.
/// </summary>
public class DistrictForwardingTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DistrictDbContext> _options;

    public DistrictForwardingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DistrictDbContext>().UseSqlite(_connection).Options;

        using var db = new DistrictDbContext(_options);
        DistrictSchema.EnsureCurrent(db);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private DistrictDbContext NewDb() => new(_options);

    // --- the fake centre ---------------------------------------------------------------

    /// <summary>
    /// Stands in for the national tier: answers the token endpoint, records
    /// every batch it is sent, and responds however the test says.
    /// </summary>
    private sealed class FakeCentral : HttpMessageHandler
    {
        private readonly List<(byte[] Body, string? Signature, CancellationToken Token)> _batches = [];

        /// <summary>Called with the 1-based attempt number; null answers 200.</summary>
        public Func<int, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }

        /// <summary>Completes when the first batch arrives.</summary>
        public TaskCompletionSource FirstReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(byte[] Body, string? Signature, CancellationToken Token)> Batches
        {
            get { lock (_batches) { return [.. _batches]; } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                return Json("""{"access_token":"node-token","expires_in":300}""");
            }

            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var signature = request.Headers.TryGetValues(DeviceSignature.HeaderName, out var values)
                ? values.Single()
                : null;

            int attempt;
            lock (_batches)
            {
                _batches.Add((body, signature, cancellationToken));
                attempt = _batches.Count;
            }

            FirstReceived.TrySetResult();

            return Respond is null
                ? Json("""{"data":{"status":"Reconciled","registered":1}}""")
                : await Respond(attempt, cancellationToken);
        }

        public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class RunningHost : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static CentralApiOptions Options(TimeSpan? timeout = null, TimeSpan? perRecord = null)
        => new()
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
            TimeoutPerRecord = perRecord ?? TimeSpan.FromMilliseconds(200),
        };

    private static CentralApiClient Client(FakeCentral centre, CentralApiOptions options)
        => new(
            new HttpClient(centre) { Timeout = Timeout.InfiniteTimeSpan },
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<CentralApiClient>.Instance);

    /// <summary>
    /// A batch as a device sends it -- deliberately with irregular whitespace,
    /// a non-ASCII name and a trailing newline, so anything that re-renders the
    /// JSON instead of carrying the bytes produces a different body.
    /// </summary>
    private static byte[] DeviceBody(Guid transactionId, int records = 1)
    {
        var entries = string.Join(",", Enumerable.Range(0, records).Select(i =>
            $$"""{ "birth" : { "brn":"{{100001 + i}}", "childFullName" : "Nyandeng Wëng" } }"""));

        return Encoding.UTF8.GetBytes(
            "{ \"meta\":{\"transactionId\":\"" + transactionId + "\"},\n  "
            + "\"data\": {\"deviceId\":\"TABLET-07\", \"facilityId\":\"" + FacilityId + "\", "
            + "\"records\":[" + entries + "]} }\n");
    }

    private static DistrictSyncController Controller(
        DistrictDbContext db,
        FakeCentral centre,
        CentralApiOptions options,
        byte[] body,
        string? signature,
        CancellationToken requestAborted = default)
    {
        var http = new DefaultHttpContext { RequestAborted = requestAborted };
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        if (signature is not null)
        {
            http.Request.Headers[DeviceSignature.HeaderName] = signature;
        }

        return new DistrictSyncController(
            db,
            Client(centre, options),
            Microsoft.Extensions.Options.Options.Create(options),
            new RunningHost(),
            NullLogger<DistrictSyncController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static int StatusOf(ActionResult<DistrictBatchResponse> result)
        => Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode ?? 200;

    // --- 11c: the signature travels with the batch -----------------------------------------

    [Fact]
    public async Task TheDevicesSignatureIsForwardedWithTheBatch()
    {
        var centre = new FakeCentral();
        var body = DeviceBody(Guid.NewGuid());

        await using var db = NewDb();
        var result = await Controller(db, centre, Options(), body, "c2lnbmF0dXJl").SubmitBatch();

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        var sent = Assert.Single(centre.Batches);
        Assert.Equal("c2lnbmF0dXJl", sent.Signature);
    }

    /// <summary>
    /// The signature covers the bytes the device sent, so the node has to send
    /// those bytes -- not an equivalent rendering of the same JSON.
    /// </summary>
    [Fact]
    public async Task TheBodyIsForwardedByteForByte()
    {
        var centre = new FakeCentral();
        var body = DeviceBody(Guid.NewGuid());

        await using var db = NewDb();
        await Controller(db, centre, Options(), body, "sig").SubmitBatch();

        Assert.Equal(body, Assert.Single(centre.Batches).Body);
    }

    /// <summary>
    /// A batch held through an outage is forwarded days later by the poller,
    /// which has only what was stored -- so the signature has to be stored.
    /// </summary>
    [Fact]
    public async Task ABatchHeldThroughAnOutageKeepsItsSignature()
    {
        var centre = new FakeCentral
        {
            Respond = (attempt, _) => attempt == 1
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(FakeCentral.Json("{}"))
        };
        var options = Options();
        var body = DeviceBody(Guid.NewGuid());

        await using (var db = NewDb())
        {
            var held = await Controller(db, centre, options, body, "c2lnbmF0dXJl").SubmitBatch();
            Assert.Equal(StatusCodes.Status202Accepted, StatusOf(held));
        }

        await using (var db = NewDb())
        {
            var batch = await db.ForwardedBatches.SingleAsync();
            Assert.Equal("c2lnbmF0dXJl", batch.DeviceSignature);

            batch.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();

            await BatchForwarder.DrainAsync(db, Client(centre, options), options, 20, NullLogger.Instance, default);
        }

        Assert.Equal(2, centre.Batches.Count);
        Assert.Equal("c2lnbmF0dXJl", centre.Batches[1].Signature);
        Assert.Equal(body, centre.Batches[1].Body);
    }

    [Fact]
    public async Task AnUnsignedBatchIsForwardedWithoutAHeader()
    {
        var centre = new FakeCentral();

        await using var db = NewDb();
        await Controller(db, centre, Options(), DeviceBody(Guid.NewGuid()), signature: null).SubmitBatch();

        Assert.Null(Assert.Single(centre.Batches).Signature);
    }

    // --- 11b: one forward at a time, and "in progress" is not "no" ----------------------------

    /// <summary>
    /// The race, reproduced: while the controller's forward is still waiting
    /// on the centre, the poller runs. It must not find the same batch due.
    /// </summary>
    [Fact]
    public async Task ABatchBeingForwardedIsNotDueToThePoller()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var centre = new FakeCentral
        {
            Respond = async (_, _) =>
            {
                await release.Task;
                return FakeCentral.Json("{}");
            }
        };
        var options = Options();

        await using var controllerDb = NewDb();
        var submitting = Controller(controllerDb, centre, options, DeviceBody(Guid.NewGuid()), "sig").SubmitBatch();

        await centre.FirstReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var pollerDb = NewDb();
        var polling = BatchForwarder.DrainAsync(
            pollerDb, Client(centre, options), options, 20, NullLogger.Instance, default);

        // A poller that found nothing due returns at once. One that picked the
        // batch up is now waiting on the centre too -- behind the same gate --
        // so give it a moment, then release both rather than hang.
        var pollerReturned = await Task.WhenAny(polling, Task.Delay(TimeSpan.FromSeconds(3))) == polling;

        release.SetResult();
        var result = await submitting;
        await polling;

        Assert.True(pollerReturned, "the poller picked up a batch whose forward was still in flight");
        Assert.Single(centre.Batches);
        Assert.Equal(ForwardedBatchStatus.Forwarded, (await NewDb().ForwardedBatches.SingleAsync()).Status);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
    }

    /// <summary>
    /// The centre's "already in progress" is a 409 with Retry-After. The batch
    /// arrived by two routes at once, or a retry overlapped the attempt before
    /// it -- either way the centre may be registering it right now.
    /// </summary>
    [Fact]
    public void InProgressAtTheCentreIsNotARefusal()
    {
        var batch = new ForwardedBatch { DeviceId = "TABLET-07", Payload = "{}" };

        BatchForwarder.Record(batch, new CentralForwardResult(
            true, 409, """{"title":"Transaction already in progress."}""", RetryAfter: TimeSpan.FromSeconds(30)));

        Assert.Equal(ForwardedBatchStatus.Queued, batch.Status);
        Assert.InRange(batch.NextAttemptAtUtc!.Value,
            DateTime.UtcNow.AddSeconds(25), DateTime.UtcNow.AddSeconds(35));
    }

    /// <summary>A 409 the centre does not invite a retry on stays a refusal.</summary>
    [Fact]
    public void AConflictWithoutRetryAfterIsStillARefusal()
    {
        var batch = new ForwardedBatch { DeviceId = "TABLET-07", Payload = "{}" };

        BatchForwarder.Record(batch, new CentralForwardResult(true, 409, "{}"));

        Assert.Equal(ForwardedBatchStatus.Rejected, batch.Status);
    }

    [Fact]
    public async Task TheClientReadsRetryAfterFromTheCentre()
    {
        var centre = new FakeCentral
        {
            Respond = (_, _) =>
            {
                var response = FakeCentral.Json("{}", HttpStatusCode.Conflict);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return Task.FromResult(response);
            }
        };

        var result = await Client(centre, Options()).ForwardAsync("{}", null, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.False(result.PermanentlyRejected);
    }

    // --- 11a: a timeout the batch can fit in -------------------------------------------------

    [Fact]
    public void TheTimeoutGrowsWithTheBatchAndDoublesAfterEachTimeout()
    {
        var options = new CentralApiOptions();

        Assert.Equal(TimeSpan.FromSeconds(25), options.AttemptTimeout(25, 0));
        Assert.Equal(TimeSpan.FromMinutes(2), options.AttemptTimeout(500, 0));
        Assert.Equal(TimeSpan.FromMinutes(4), options.AttemptTimeout(500, 1));
        Assert.Equal(TimeSpan.FromMinutes(10), options.AttemptTimeout(500, 10));
    }

    /// <summary>
    /// A centre that is working but slow is not an outage, and says so: it is
    /// counted, and described in words that do not send someone to check a
    /// link that is up.
    /// </summary>
    [Fact]
    public async Task ATimeoutIsReportedAsSlownessNotAsAnOutage()
    {
        var centre = new FakeCentral
        {
            Respond = async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return FakeCentral.Json("{}");
            }
        };

        var result = await Client(centre, Options()).ForwardAsync("{}", null, TimeSpan.FromMilliseconds(200));

        Assert.True(result.TimedOut);
        Assert.False(result.Reached);

        var batch = new ForwardedBatch { DeviceId = "TABLET-07", Payload = "{}" };
        BatchForwarder.Record(batch, result);

        Assert.Equal(ForwardedBatchStatus.Queued, batch.Status);
        Assert.Equal(1, batch.ConsecutiveTimeouts);
        Assert.Contains("did not finish", batch.LastError);
    }

    /// <summary>
    /// The livelock, ended: a batch that did not fit in its first attempt is
    /// given longer on the next, and lands.
    /// </summary>
    [Fact]
    public async Task ABatchThatTimedOutIsGivenLongerAndLands()
    {
        var centre = new FakeCentral
        {
            Respond = async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), token);
                return FakeCentral.Json("{}");
            }
        };
        var options = Options(timeout: TimeSpan.FromMilliseconds(400), perRecord: TimeSpan.Zero);

        await using var db = NewDb();
        var batch = new ForwardedBatch
        {
            TransactionId = Guid.NewGuid(), DeviceId = "TABLET-07", FacilityId = FacilityId,
            RecordCount = 1, Payload = "{}"
        };
        db.ForwardedBatches.Add(batch);
        await db.SaveChangesAsync();

        var first = await BatchForwarder.ForwardAsync(db, Client(centre, options), options, batch, default);
        Assert.True(first.TimedOut);

        var second = await BatchForwarder.ForwardAsync(db, Client(centre, options), options, batch, default);

        Assert.True(second.Accepted);
        Assert.Equal(ForwardedBatchStatus.Forwarded, batch.Status);
        Assert.Equal(0, batch.ConsecutiveTimeouts);
    }

    /// <summary>
    /// The node promises to deliver its backlog in the order it was taken. A
    /// batch that timed out is still the one in front.
    /// </summary>
    [Fact]
    public async Task ATimeoutHoldsTheQueueInOrder()
    {
        var centre = new FakeCentral
        {
            Respond = async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return FakeCentral.Json("{}");
            }
        };
        var options = Options(timeout: TimeSpan.FromMilliseconds(200), perRecord: TimeSpan.Zero);

        await using var db = NewDb();
        db.ForwardedBatches.AddRange(
            new ForwardedBatch
            {
                TransactionId = Guid.NewGuid(), DeviceId = "TABLET-07", FacilityId = FacilityId,
                Payload = "{}", ReceivedAtUtc = DateTime.UtcNow.AddHours(-2)
            },
            new ForwardedBatch
            {
                TransactionId = Guid.NewGuid(), DeviceId = "TABLET-07", FacilityId = FacilityId,
                Payload = "{}", ReceivedAtUtc = DateTime.UtcNow.AddHours(-1)
            });
        await db.SaveChangesAsync();

        await BatchForwarder.DrainAsync(db, Client(centre, options), options, 20, NullLogger.Instance, default);

        Assert.Single(centre.Batches);
    }

    /// <summary>
    /// A village link dropping mid-request must not cancel the forward:
    /// cancelling it cancels the centre's transaction, which discards the whole
    /// batch's work. The node already holds the batch; it finishes the job.
    /// </summary>
    [Fact]
    public async Task ADeviceDisconnectingDoesNotCancelTheForward()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var centre = new FakeCentral
        {
            Respond = async (_, _) =>
            {
                await release.Task;
                return FakeCentral.Json("{}");
            }
        };
        using var device = new CancellationTokenSource();

        await using var db = NewDb();
        var submitting = Controller(db, centre, Options(), DeviceBody(Guid.NewGuid()), "sig", device.Token)
            .SubmitBatch();

        await centre.FirstReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await device.CancelAsync();
        release.SetResult();
        await submitting;

        Assert.False(Assert.Single(centre.Batches).Token.IsCancellationRequested);
        Assert.Equal(ForwardedBatchStatus.Forwarded, (await NewDb().ForwardedBatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task TheNodeStatusCountsBatchesSlowToFinishApartFromTheQueue()
    {
        await using var db = NewDb();
        db.ForwardedBatches.AddRange(
            new ForwardedBatch { TransactionId = Guid.NewGuid(), DeviceId = "A", Payload = "{}", ConsecutiveTimeouts = 2 },
            new ForwardedBatch { TransactionId = Guid.NewGuid(), DeviceId = "B", Payload = "{}" });
        await db.SaveChangesAsync();

        var status = (await Controller(db, new FakeCentral(), Options(), [], null).Status()).Value!;

        Assert.Equal(2, status.Queued);
        Assert.Equal(1, status.SlowToFinish);
    }

    // --- the store: an existing node is upgraded in place -------------------------------------

    /// <summary>
    /// A node deployed before these columns existed holds births in transit
    /// that exist nowhere else. It must gain the columns without losing a row.
    /// </summary>
    [Fact]
    public void AnExistingStoreGainsTheNewColumnsAndKeepsItsBatches()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            // The table as EnsureCreated built it before this change.
            create.CommandText = """
                CREATE TABLE "ForwardedBatches" (
                    "ForwardedBatchId" TEXT NOT NULL CONSTRAINT "PK_ForwardedBatches" PRIMARY KEY,
                    "TransactionId" TEXT NOT NULL, "DeviceId" TEXT NOT NULL, "FacilityId" TEXT NOT NULL,
                    "RecordCount" INTEGER NOT NULL, "Payload" TEXT NOT NULL, "Status" TEXT NOT NULL,
                    "ReceivedAtUtc" TEXT NOT NULL, "ForwardedAtUtc" TEXT NULL, "Attempts" INTEGER NOT NULL,
                    "LastAttemptAtUtc" TEXT NULL, "NextAttemptAtUtc" TEXT NULL, "CentralResponse" TEXT NULL,
                    "CentralStatusCode" INTEGER NULL, "LastError" TEXT NULL);
                INSERT INTO "ForwardedBatches" VALUES (
                    '0199a1b2-0000-7000-8000-000000000009', '0199a1b2-0000-7000-8000-00000000000a', 'TABLET-07',
                    '0199a1b2-0001-7000-8000-000000000001', 3, '{"held":true}', 'Queued',
                    '2026-09-20 08:00:00', NULL, 4, NULL, NULL, NULL, NULL, 'down');
                """;
            create.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<DistrictDbContext>().UseSqlite(connection).Options;

        using (var db = new DistrictDbContext(options))
        {
            DistrictSchema.EnsureCurrent(db);
            DistrictSchema.EnsureCurrent(db); // idempotent
        }

        using (var db = new DistrictDbContext(options))
        {
            var held = db.ForwardedBatches.Single();

            Assert.Equal("""{"held":true}""", held.Payload);
            Assert.Equal(4, held.Attempts);
            Assert.Null(held.DeviceSignature);
            Assert.Equal(0, held.ConsecutiveTimeouts);
        }
    }
}
