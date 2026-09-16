using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Kafka;

/// <summary>
/// Delivers staged events to Kafka.
///
/// Delivery is at-least-once, not exactly-once: a message acknowledged by
/// the broker immediately before this process dies will be sent again by
/// whoever picks up the lease. That is the correct trade for a registry --
/// a consumer seeing an event twice can deduplicate on the BRN, whereas a
/// birth that never reaches the statistics tier is invisible. Consumers must
/// therefore be idempotent.
/// </summary>
public class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    KafkaOutboxTransport transport,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DispatchedRetention = TimeSpan.FromDays(7);
    private const int BatchSize = 50;

    /// <summary>
    /// Identifies this relay instance in the lease, so a stuck message can be
    /// traced to the process that was holding it.
    /// </summary>
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        do
        {
            try
            {
                await PumpAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                // The relay must survive anything: an unhandled failure here
                // would silently stop every event in the system.
                logger.LogError(ex, "Outbox relay pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NcbrsDbContext>();

        var batch = await ClaimAsync(db, cancellationToken);

        foreach (var message in batch)
        {
            var result = await transport.DispatchAsync(
                message.Topic, message.PartitionKey, message.Payload,
                message.OutboxMessageId, cancellationToken);

            message.AttemptCount++;

            if (result.Delivered)
            {
                message.DispatchedAtUtc = DateTime.UtcNow;
                message.LastError = null;
                message.LockedUntilUtc = null;
            }
            else
            {
                // Left unlocked so the next pass retries it. Kafka being
                // unreachable is the expected state for long stretches here,
                // so this is logged at Debug rather than as an incident.
                message.LastError = result.Error;
                message.LockedUntilUtc = null;

                logger.LogDebug(
                    "Outbox message {Id} not delivered (attempt {Attempt}): {Error}",
                    message.OutboxMessageId, message.AttemptCount, result.Error);
            }
        }

        if (batch.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);

            var delivered = batch.Count(message => message.DispatchedAtUtc is not null);
            if (delivered > 0)
            {
                logger.LogInformation("Outbox relay dispatched {Delivered}/{Total}", delivered, batch.Count);
            }
        }

        await PurgeAsync(db, cancellationToken);
    }

    /// <summary>
    /// Takes a lease on a batch so several API instances do not each publish
    /// the same message. The lease expires, so a relay that dies mid-batch
    /// releases its claim rather than stranding the events forever.
    /// </summary>
    private async Task<List<OutboxMessage>> ClaimAsync(NcbrsDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var candidates = await db.OutboxMessages
            .Where(message => message.DispatchedAtUtc == null
                              && (message.LockedUntilUtc == null || message.LockedUntilUtc < now))
            .OrderBy(message => message.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return candidates;
        }

        foreach (var message in candidates)
        {
            message.LockedUntilUtc = now.Add(LeaseDuration);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return candidates;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another relay claimed some of these first. Back off and let the
            // next pass pick up whatever is genuinely free.
            db.ChangeTracker.Clear();
            return [];
        }
    }

    /// <summary>
    /// Delivered messages are kept briefly rather than deleted on dispatch,
    /// so an operator investigating a consumer problem can see exactly what
    /// was produced and when.
    /// </summary>
    private static async Task PurgeAsync(NcbrsDbContext db, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.Subtract(DispatchedRetention);

        await db.OutboxMessages
            .Where(message => message.DispatchedAtUtc != null && message.DispatchedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
