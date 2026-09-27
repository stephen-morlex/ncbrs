using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCBRS.Data;

namespace NCBRS.Services;

public class OperationalHealthOptions
{
    public const string SectionName = "OperationalHealth";

    /// <summary>
    /// How long an event may wait in the outbox before that is a problem. The
    /// Relay polls every few seconds, so anything older than this is not a
    /// queue working through a burst; it's a Relay that stopped, or a broker it
    /// cannot reach.
    /// </summary>
    public TimeSpan OutboxStaleAfter { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>What the outbox says about delivery to Kafka.</summary>
public record OutboxHealth(
    int Pending,
    DateTime? OldestPendingCreatedAtUtc,
    int PendingWithErrors,
    DateTime? LastDispatchedAtUtc);

/// <summary>
/// What Postgres says about WAL archiving, from <c>pg_stat_archiver</c>.
/// </summary>
public record WalArchiveHealth(
    bool Enabled,
    long ArchivedCount,
    long FailedCount,
    DateTime? LastArchivedAtUtc,
    DateTime? LastFailedAtUtc,
    bool FailingNow);

/// <param name="Status">"ok", or "degraded" with <paramref name="Problems"/> saying why.</param>
/// <param name="WalArchive">Null where the database has no WAL archive (SQLite): not applicable, never zero.</param>
public record OperationalHealth(
    string Status,
    IReadOnlyList<string> Problems,
    OutboxHealth Outbox,
    WalArchiveHealth? WalArchive);

/// <summary>
/// The signals the RUNBOOK tells an operator to alert on, where a monitor can
/// read them (plan §17 item 19).
///
/// Both are failures a user never sees. WAL archiving that fails breaks
/// nothing: registrations keep succeeding, and the absence of any recovery
/// point is discovered on the day it is needed (it happened here: 18 failed
/// attempts, zero segments). An outbox that stops draining breaks nothing
/// either: registrations keep succeeding, and the dashboard quietly stops
/// moving, which reads as a quiet week. Before this, noticing either meant
/// someone querying the database by hand.
///
/// Reports what the database says and nothing it cannot know. A monitoring
/// system decides when to page; this makes sure it has something to decide on.
/// </summary>
public class OperationalHealthService(NcbrsDbContext db, IOptions<OperationalHealthOptions> options)
{
    public async Task<OperationalHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        var outbox = await OutboxAsync(cancellationToken);
        var archive = await WalArchiveAsync(cancellationToken);
        var problems = ProblemsIn(outbox, archive, options.Value.OutboxStaleAfter, DateTime.UtcNow);

        return new OperationalHealth(problems.Count == 0 ? "ok" : "degraded", problems, outbox, archive);
    }

    /// <summary>
    /// The verdicts, apart from the reading. Kept pure so every branch can be
    /// tested: the archive ones otherwise need a server whose archiving is
    /// actually broken.
    /// </summary>
    public static IReadOnlyList<string> ProblemsIn(
        OutboxHealth outbox, WalArchiveHealth? archive, TimeSpan outboxStaleAfter, DateTime nowUtc)
    {
        var problems = new List<string>();

        if (outbox.OldestPendingCreatedAtUtc is { } oldest && nowUtc - oldest > outboxStaleAfter)
        {
            problems.Add(
                $"{outbox.Pending} event(s) waiting in the outbox, the oldest since {oldest:u}: "
                + "the Relay is not running or cannot reach Kafka. Registrations are unaffected; "
                + "the dashboard and every downstream consumer are falling behind.");
        }

        if (archive is { Enabled: false })
        {
            problems.Add("WAL archiving is off: there is no point-in-time recovery for this database.");
        }
        else if (archive is { FailingNow: true })
        {
            problems.Add(
                $"WAL archiving is failing (last failure {archive.LastFailedAtUtc:u}, "
                + $"last success {(archive.LastArchivedAtUtc is { } ok ? ok.ToString("u") : "never")}): "
                + "no recovery point is being kept. See RUNBOOK, \"WAL archiving is failing\".");
        }

        return problems;
    }

    /// <summary>
    /// Failing now, not failed once. <c>failed_count</c> is cumulative since the
    /// statistics were last reset, so a failure last month that archiving has
    /// since recovered from must not page anyone today. What matters is whether
    /// the most recent attempt failed.
    /// </summary>
    public static bool IsFailingNow(DateTime? lastArchivedAtUtc, DateTime? lastFailedAtUtc)
        => lastFailedAtUtc is not null && (lastArchivedAtUtc is null || lastFailedAtUtc > lastArchivedAtUtc);

    private async Task<OutboxHealth> OutboxAsync(CancellationToken cancellationToken)
    {
        var pending = db.OutboxMessages.AsNoTracking().Where(message => message.DispatchedAtUtc == null);

        return new OutboxHealth(
            await pending.CountAsync(cancellationToken),
            await pending.MinAsync(message => (DateTime?)message.CreatedAtUtc, cancellationToken),
            await pending.CountAsync(message => message.LastError != null, cancellationToken),
            await db.OutboxMessages.AsNoTracking()
                .MaxAsync(message => message.DispatchedAtUtc, cancellationToken));
    }

    /// <summary>Null on a database with no WAL archive to report on.</summary>
    private async Task<WalArchiveHealth?> WalArchiveAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return null;
        }

        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            // pg_stat_archiver is readable by any role, so this needs no
            // privilege the application role does not already have.
            command.CommandText = """
                SELECT current_setting('archive_mode') <> 'off',
                       archived_count, failed_count, last_archived_time, last_failed_time
                FROM pg_stat_archiver
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            var lastArchived = Utc(reader, 3);
            var lastFailed = Utc(reader, 4);

            return new WalArchiveHealth(
                Enabled: reader.GetBoolean(0),
                ArchivedCount: reader.GetInt64(1),
                FailedCount: reader.GetInt64(2),
                LastArchivedAtUtc: lastArchived,
                LastFailedAtUtc: lastFailed,
                FailingNow: IsFailingNow(lastArchived, lastFailed));
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static DateTime? Utc(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTime.SpecifyKind(reader.GetFieldValue<DateTimeOffset>(ordinal).UtcDateTime, DateTimeKind.Utc);
}
