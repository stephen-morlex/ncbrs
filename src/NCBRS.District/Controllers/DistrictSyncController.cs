using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCBRS.Devices;
using NCBRS.District.Data;
using NCBRS.District.Models;
using NCBRS.District.Services;
using NCBRS.Models;

namespace NCBRS.District.Controllers;

/// <summary>
/// What a facility device talks to when it reaches its district office
/// instead of the national tier (draft 6.2, 6.3).
///
/// The path matches the central API's, so a device points at whichever it can
/// reach without knowing it is talking to a different tier. What differs is
/// the answer it can get: the centre can say what became of each record,
/// while a node that has not reached the centre yet can only say it is
/// holding the batch. That difference is made explicit rather than papered
/// over — a device told "queued" must not tell a family the registration is
/// confirmed.
/// </summary>
[ApiController]
[Route("api/Sync")]
[Produces("application/json")]
public class DistrictSyncController(
    DistrictDbContext db,
    CentralApiClient central,
    IOptions<CentralApiOptions> centralOptions,
    IHostApplicationLifetime lifetime,
    ILogger<DistrictSyncController> logger) : ControllerBase
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Takes a batch from a device and tries the centre at once.
    ///
    /// The body is read as the bytes that arrived, not bound as JSON and
    /// re-rendered: those bytes are what the device's signature covers, and
    /// the centre verifies them exactly. The signature header is kept beside
    /// them and forwarded unchanged.
    /// </summary>
    [HttpPost("batches")]
    [ProducesResponseType(typeof(DistrictBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DistrictBatchResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DistrictBatchResponse>> SubmitBatch()
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, HttpContext.RequestAborted);
        var bytes = buffer.ToArray();

        JsonDocument document;
        try
        {
            var json = bytes.AsMemory();
            document = JsonDocument.Parse(json.Span.StartsWith(Utf8Bom) ? json[Utf8Bom.Length..] : json);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "The body is not valid JSON." });
        }

        using var _ = document;
        var envelope = document.RootElement;

        // Lossless for valid UTF-8, so re-encoding on the way out reproduces
        // the bytes that arrived. A body that is not valid UTF-8 would not
        // survive -- and its signature then fails at the centre, which is the
        // right outcome for a body nobody can read the same way twice.
        var payload = Encoding.UTF8.GetString(bytes);

        if (envelope.ValueKind != JsonValueKind.Object
            || !TryRead(envelope, out var transactionId, out var deviceId, out var facilityId, out var records))
        {
            return BadRequest(new
            {
                error = "The batch must carry meta.transactionId, data.deviceId and data.facilityId."
            });
        }

        // The same submission arriving twice -- a device retrying because it
        // never saw the response -- is the one it already has, not a second
        // batch. The centre enforces this too; doing it here as well means the
        // guarantee holds at whichever tier the device reached.
        var existing = await db.ForwardedBatches
            .FirstOrDefaultAsync(batch => batch.TransactionId == transactionId, HttpContext.RequestAborted);

        if (existing is not null)
        {
            return Answer(existing);
        }

        var signature = Request.Headers[DeviceSignature.HeaderName].ToString();

        var stored = new ForwardedBatch
        {
            TransactionId = transactionId,
            DeviceId = deviceId,
            FacilityId = facilityId,
            RecordCount = records,
            Payload = payload,
            DeviceSignature = string.IsNullOrEmpty(signature) ? null : signature
        };

        // Claimed in the same write that stores it, so there is no moment at
        // which the poller can see it as due while the forward below runs.
        BatchForwarder.Claim(stored, centralOptions.Value.AttemptTimeout(records, 0));

        db.ForwardedBatches.Add(stored);
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        // Tried immediately so a district that does have a link gives the
        // device the centre's real answer -- which BRNs were confirmed, which
        // records were duplicates -- rather than making it come back for it.
        //
        // Bound to the node's lifetime, not the device's connection. The batch
        // is already held, and a village link dropping mid-request must not
        // cancel the forward: cancelling it cancels the centre's transaction,
        // which throws away the whole batch's work.
        var result = await BatchForwarder.ForwardAsync(
            db, central, centralOptions.Value, stored, lifetime.ApplicationStopping);

        if (!result.Reached)
        {
            logger.LogInformation(
                result.TimedOut
                    ? "Held batch {TransactionId} from device {DeviceId}: central tier did not finish it in time"
                    : "Held batch {TransactionId} from device {DeviceId}: central tier unreachable",
                transactionId, deviceId);
        }

        return Answer(stored);
    }

    /// <summary>
    /// What became of a batch the node had to queue.
    ///
    /// A device told "queued" comes back to this once it has a link again --
    /// or its district visit the following week -- to find out which of its
    /// records registered, which were already held, and which failed.
    /// </summary>
    [HttpGet("batches/{transactionId:guid}")]
    [ProducesResponseType(typeof(DistrictBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DistrictBatchResponse>> GetBatch(Guid transactionId)
    {
        var batch = await db.ForwardedBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.TransactionId == transactionId, HttpContext.RequestAborted);

        return batch is null
            ? NotFound(new { error = $"No batch with transaction id '{transactionId}' has reached this district node." })
            : Answer(batch);
    }

    /// <summary>
    /// Whether this node is currently reaching the centre, and how much it is
    /// holding.
    ///
    /// The number a district officer actually needs during an outage: a queue
    /// that is growing is normal, one that stops draining after the link
    /// returns is not.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(DistrictNodeStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<DistrictNodeStatus>> Status()
    {
        var queued = await db.ForwardedBatches
            .CountAsync(batch => batch.Status == ForwardedBatchStatus.Queued, HttpContext.RequestAborted);

        var oldest = await db.ForwardedBatches
            .Where(batch => batch.Status == ForwardedBatchStatus.Queued)
            .OrderBy(batch => batch.ReceivedAtUtc)
            .Select(batch => (DateTime?)batch.ReceivedAtUtc)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        var lastForwarded = await db.ForwardedBatches
            .Where(batch => batch.ForwardedAtUtc != null)
            .MaxAsync(batch => (DateTime?)batch.ForwardedAtUtc, HttpContext.RequestAborted);

        return new DistrictNodeStatus(
            queued,
            await db.ForwardedBatches.CountAsync(
                batch => batch.Status == ForwardedBatchStatus.Forwarded, HttpContext.RequestAborted),
            await db.ForwardedBatches.CountAsync(
                batch => batch.Status == ForwardedBatchStatus.Rejected, HttpContext.RequestAborted),
            oldest,
            lastForwarded,
            await db.ForwardedBatches.CountAsync(
                batch => batch.Status == ForwardedBatchStatus.Queued && batch.ConsecutiveTimeouts > 0,
                HttpContext.RequestAborted));
    }

    private ActionResult<DistrictBatchResponse> Answer(ForwardedBatch batch)
    {
        var response = new DistrictBatchResponse(
            batch.TransactionId,
            batch.Status,
            batch.RecordCount,
            batch.ReceivedAtUtc,
            batch.ForwardedAtUtc,
            batch.Attempts,
            batch.CentralResponse is null ? null : JsonDocument.Parse(batch.CentralResponse).RootElement,
            batch.LastError);

        // 202 while it is only held: the district has the batch, the register
        // does not, and the distinction has to survive into the status code a
        // device branches on.
        return batch.Status == ForwardedBatchStatus.Queued
            ? StatusCode(StatusCodes.Status202Accepted, response)
            : Ok(response);
    }

    private static bool TryRead(
        JsonElement envelope,
        out Guid transactionId,
        out string deviceId,
        out Guid facilityId,
        out int records)
    {
        transactionId = Guid.Empty;
        deviceId = string.Empty;
        facilityId = Guid.Empty;
        records = 0;

        if (!envelope.TryGetProperty("meta", out var meta)
            || !meta.TryGetProperty("transactionId", out var transaction)
            || !transaction.TryGetGuid(out transactionId)
            || transactionId == Guid.Empty)
        {
            // Required here, unlike at the centre where one is generated. The
            // node has nothing else to deduplicate a retry on, and a device
            // that cannot name its submission cannot be told what became of
            // it either.
            return false;
        }

        if (!envelope.TryGetProperty("data", out var data))
        {
            return false;
        }

        if (data.TryGetProperty("deviceId", out var device))
        {
            deviceId = device.GetString() ?? string.Empty;
        }

        if (data.TryGetProperty("facilityId", out var facility))
        {
            facility.TryGetGuid(out facilityId);
        }

        if (data.TryGetProperty("records", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            records = list.GetArrayLength();
        }

        return !string.IsNullOrWhiteSpace(deviceId) && facilityId != Guid.Empty;
    }
}

/// <summary>
/// What the district node can say about a batch.
///
/// Carries the centre's own response verbatim once there is one, so a device
/// parses one shape whether it reached the centre directly or came through
/// here a week later.
/// </summary>
public record DistrictBatchResponse(
    Guid TransactionId,
    ForwardedBatchStatus Status,
    int RecordCount,
    DateTime ReceivedAtUtc,
    DateTime? ForwardedAtUtc,
    int Attempts,
    JsonElement? Central,
    string? LastError
);

/// <param name="SlowToFinish">
/// Queued batches whose last attempt reached the centre but ran out of time
/// before it finished. Not an outage -- the link is up -- and counted apart so
/// nobody is sent to check one: each retry allows longer.
/// </param>
public record DistrictNodeStatus(
    int Queued,
    int Forwarded,
    int Rejected,
    DateTime? OldestQueuedAtUtc,
    DateTime? LastForwardedAtUtc,
    int SlowToFinish
);
