using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Drops idempotency keys once they're past the replay window. Without this
/// the table grows for the life of the system, since every successful
/// state-changing request adds a row.
///
/// The window is deliberately generous: a village device can be offline for
/// weeks, and it should still be able to retry a pending sync and receive
/// the original answer rather than a duplicate registration. Note this only
/// removes the ability to *replay* an old response -- RequestLog and
/// AuditLog keep the permanent record.
/// </summary>
public class IdempotencyPurgeService(
    IServiceScopeFactory scopeFactory,
    ILogger<IdempotencyPurgeService> logger) : BackgroundService
{
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        do
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                // Housekeeping must never take the API down with it.
                logger.LogError(ex, "Idempotency purge sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NcbrsDbContext>();

        var cutoff = DateTime.UtcNow.Subtract(ReplayWindow);

        var removed = await db.IdempotencyRecords
            .Where(record => record.Status == IdempotencyStatus.Completed && record.CompletedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
        {
            logger.LogInformation("Purged {Count} idempotency records older than {Cutoff:u}", removed, cutoff);
        }
    }
}
