using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCBRS.District.Data;
using NCBRS.District.Models;

namespace NCBRS.District.Services;

public class ForwarderOptions
{
    public const string SectionName = "Forwarder";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Backoff between attempts on one batch, capped. A district link that
    /// has been down all night should not come back to a node hammering it.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many batches to push per cycle.</summary>
    public int BatchSize { get; set; } = 20;
}

/// <summary>
/// Drains the district queue to the national tier (draft 6.2, 7.2).
///
/// Ordered oldest-first and one at a time. The centre processes a batch per
/// record with its own savepoints, so throughput is not the constraint here
/// -- what matters is that a district reconnecting after an outage delivers
/// its backlog in the order it was taken, so the register reflects the
/// sequence events actually happened in.
/// </summary>
public class BatchForwarder(
    IServiceScopeFactory scopes,
    IOptions<ForwarderOptions> options,
    ILogger<BatchForwarder> logger) : BackgroundService
{
    /// <summary>
    /// How far past an attempt's own timeout its claim on a batch extends: the
    /// time to record the outcome once the answer is in.
    /// </summary>
    private static readonly TimeSpan ClaimMargin = TimeSpan.FromMinutes(1);

    private readonly ForwarderOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "District forwarder started; polling every {Interval}", _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();

                await DrainAsync(
                    scope.ServiceProvider.GetRequiredService<DistrictDbContext>(),
                    scope.ServiceProvider.GetRequiredService<CentralApiClient>(),
                    scope.ServiceProvider.GetRequiredService<IOptions<CentralApiOptions>>().Value,
                    _options.BatchSize,
                    logger,
                    stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A node that stops forwarding silently is the worst failure
                // here: registrations keep being accepted and nothing ever
                // leaves. Log and keep the loop alive.
                logger.LogError(ex, "District forwarding cycle failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass over the batches that are due.</summary>
    public static async Task DrainAsync(
        DistrictDbContext db,
        CentralApiClient central,
        CentralApiOptions centralOptions,
        int take,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var due = await db.ForwardedBatches
            .Where(batch => batch.Status == ForwardedBatchStatus.Queued
                            && (batch.NextAttemptAtUtc == null || batch.NextAttemptAtUtc <= now))
            .OrderBy(batch => batch.ReceivedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        foreach (var batch in due)
        {
            var result = await ForwardAsync(db, central, centralOptions, batch, cancellationToken);

            if (!result.Reached)
            {
                // Stop here rather than working through the rest of the queue
                // -- and keep the ordering promise. For a link that is down
                // the rest would fail identically. For a timeout the centre is
                // up but this batch is the one in front, and it goes first
                // next time, with a longer timeout.
                logger.LogInformation(
                    result.TimedOut
                        ? "Central tier slow to finish a batch; {Remaining} batch(es) still queued behind it"
                        : "Central tier unreachable; {Remaining} batch(es) still queued",
                    await db.ForwardedBatches.CountAsync(
                        entry => entry.Status == ForwardedBatchStatus.Queued, cancellationToken));

                return;
            }
        }
    }

    /// <summary>
    /// Claims a batch, forwards it, and records what happened. The one path
    /// every forward takes, whether the controller pushes a batch the moment
    /// it arrives or the poller retries it later.
    ///
    /// The claim is saved before the request goes out. Without it the two
    /// paths raced: the controller saved a new batch as due and forwarded it,
    /// the poller found the same row due and forwarded it again, and the
    /// centre's answer to the second copy -- "already in progress" -- was
    /// recorded as a refusal of a batch it had registered.
    /// </summary>
    public static async Task<CentralForwardResult> ForwardAsync(
        DistrictDbContext db,
        CentralApiClient central,
        CentralApiOptions centralOptions,
        ForwardedBatch batch,
        CancellationToken cancellationToken)
    {
        var timeout = centralOptions.AttemptTimeout(batch.RecordCount, batch.ConsecutiveTimeouts);

        Claim(batch, timeout);
        await db.SaveChangesAsync(cancellationToken);

        var result = await central.ForwardAsync(batch.Payload, batch.DeviceSignature, timeout, cancellationToken);

        Record(batch, result);
        await db.SaveChangesAsync(cancellationToken);

        return result;
    }

    /// <summary>
    /// Takes a batch out of the due set for as long as an attempt with this
    /// timeout can take. If the node dies mid-forward the claim lapses and the
    /// batch is simply due again.
    /// </summary>
    public static void Claim(ForwardedBatch batch, TimeSpan timeout)
        => batch.NextAttemptAtUtc = DateTime.UtcNow.Add(timeout).Add(ClaimMargin);

    /// <summary>
    /// Applies one attempt's outcome. Shared with the synchronous path the
    /// controller takes when it tries to forward immediately, so a batch is
    /// recorded the same way whichever route pushed it.
    /// </summary>
    public static void Record(ForwardedBatch batch, CentralForwardResult result)
    {
        batch.Attempts++;
        batch.LastAttemptAtUtc = DateTime.UtcNow;

        if (result.Accepted)
        {
            batch.Status = ForwardedBatchStatus.Forwarded;
            batch.ForwardedAtUtc = batch.LastAttemptAtUtc;
            batch.CentralStatusCode = result.StatusCode;
            batch.CentralResponse = result.Body;
            batch.NextAttemptAtUtc = null;
            batch.LastError = null;
            batch.ConsecutiveTimeouts = 0;

            return;
        }

        if (result.PermanentlyRejected)
        {
            // Retrying will not change a 4xx. Held as Rejected rather than
            // discarded: the district has to be able to see what was refused
            // and tell the facility why.
            batch.Status = ForwardedBatchStatus.Rejected;
            batch.CentralStatusCode = result.StatusCode;
            batch.CentralResponse = result.Body;
            batch.NextAttemptAtUtc = null;
            batch.LastError = $"Central tier refused the batch with {result.StatusCode}.";
            batch.ConsecutiveTimeouts = 0;

            return;
        }

        if (result.TimedOut)
        {
            // Lengthens the next attempt (CentralApiOptions.AttemptTimeout).
            // Reported in its own words, because "the centre did not answer"
            // sends someone to check a link that is working.
            batch.ConsecutiveTimeouts++;
        }
        else if (result.Reached)
        {
            batch.ConsecutiveTimeouts = 0;
        }

        batch.LastError = result.Error ?? $"Central tier returned {result.StatusCode}.";

        // The centre's own estimate of when to come back, where it gave one --
        // "already in progress" resolves when the other attempt finishes, not
        // on this node's backoff schedule.
        batch.NextAttemptAtUtc = DateTime.UtcNow.Add(result.RetryAfter ?? Backoff(batch.Attempts));
    }

    /// <summary>Doubling, capped. Kept static so the controller shares it.</summary>
    private static TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(
            30d * Math.Pow(2, Math.Max(0, attempts - 1)),
            TimeSpan.FromMinutes(15).TotalSeconds);

        return TimeSpan.FromSeconds(seconds);
    }
}
