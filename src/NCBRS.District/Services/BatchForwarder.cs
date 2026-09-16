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
    private readonly ForwarderOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "District forwarder started; polling every {Interval}", _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
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

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DistrictDbContext>();
        var central = scope.ServiceProvider.GetRequiredService<CentralApiClient>();

        var now = DateTime.UtcNow;

        var due = await db.ForwardedBatches
            .Where(batch => batch.Status == ForwardedBatchStatus.Queued
                            && (batch.NextAttemptAtUtc == null || batch.NextAttemptAtUtc <= now))
            .OrderBy(batch => batch.ReceivedAtUtc)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var batch in due)
        {
            var result = await central.ForwardAsync(batch.Payload, cancellationToken);

            Record(batch, result);
            await db.SaveChangesAsync(cancellationToken);

            if (!result.Reached)
            {
                // The link is down. Stop here rather than working through the
                // rest of the queue to fail identically -- and keep the
                // ordering promise for when it returns.
                logger.LogInformation(
                    "Central tier unreachable; {Remaining} batch(es) still queued",
                    await db.ForwardedBatches.CountAsync(
                        entry => entry.Status == ForwardedBatchStatus.Queued, cancellationToken));

                return;
            }
        }
    }

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

            return;
        }

        batch.LastError = result.Error ?? $"Central tier returned {result.StatusCode}.";
        batch.NextAttemptAtUtc = DateTime.UtcNow.Add(Backoff(batch.Attempts));
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
