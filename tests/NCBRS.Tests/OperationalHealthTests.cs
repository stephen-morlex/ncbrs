using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Npgsql;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The API's <c>/health</c> (plan §17 item 19): the two faults the RUNBOOK
/// says must be alerted on, because neither is visible to anyone using the
/// system -- an outbox that stopped draining, and WAL archiving that is off
/// or failing.
/// </summary>
public class OperationalHealthTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static OperationalHealthService Service(NcbrsDbContext db, TimeSpan? staleAfter = null)
        => new(db, Options.Create(new OperationalHealthOptions
        {
            OutboxStaleAfter = staleAfter ?? TimeSpan.FromMinutes(2),
        }));

    private static OutboxMessage Message(DateTime createdAtUtc, DateTime? dispatchedAtUtc = null, string? error = null)
        => new()
        {
            Topic = "ncbrs.birth-records.registered",
            Payload = "{}",
            CreatedAtUtc = createdAtUtc,
            DispatchedAtUtc = dispatchedAtUtc,
            LastError = error,
        };

    // --- the verdicts, every branch -----------------------------------------------------

    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly OutboxHealth Drained = new(0, null, 0, Now.AddMinutes(-1));

    private static WalArchiveHealth Archive(bool enabled = true, DateTime? lastArchived = null, DateTime? lastFailed = null)
        => new(enabled, 100, lastFailed is null ? 0 : 3, lastArchived, lastFailed,
            OperationalHealthService.IsFailingNow(lastArchived, lastFailed));

    [Fact]
    public void ArchivingThatWorksIsNotAProblem()
        => Assert.Empty(OperationalHealthService.ProblemsIn(
            Drained, Archive(lastArchived: Now.AddMinutes(-1)), TimeSpan.FromMinutes(2), Now));

    /// <summary>The case the RUNBOOK was written about: failing, silently, right now.</summary>
    [Fact]
    public void ArchivingThatIsFailingNowIsAProblem()
    {
        var problems = OperationalHealthService.ProblemsIn(
            Drained, Archive(lastArchived: Now.AddHours(-3), lastFailed: Now.AddMinutes(-1)), TimeSpan.FromMinutes(2), Now);

        Assert.Contains(problems, problem => problem.Contains("WAL archiving is failing"));
    }

    [Fact]
    public void ArchivingThatHasNeverSucceededIsFailing()
        => Assert.True(OperationalHealthService.IsFailingNow(lastArchivedAtUtc: null, lastFailedAtUtc: Now));

    /// <summary>
    /// failed_count only ever grows. A failure archiving has since recovered
    /// from is history, and must not page anyone today.
    /// </summary>
    [Fact]
    public void AFailureArchivingHasRecoveredFromIsNotAProblem()
        => Assert.Empty(OperationalHealthService.ProblemsIn(
            Drained, Archive(lastArchived: Now.AddMinutes(-1), lastFailed: Now.AddDays(-30)), TimeSpan.FromMinutes(2), Now));

    /// <summary>Off is the silent case: no recovery point, and nothing failing to say so.</summary>
    [Fact]
    public void ArchivingThatIsOffIsAProblem()
        => Assert.Contains(
            OperationalHealthService.ProblemsIn(Drained, Archive(enabled: false), TimeSpan.FromMinutes(2), Now),
            problem => problem.Contains("WAL archiving is off"));

    [Fact]
    public void NoArchiveToReportOnIsNotAProblem()
        => Assert.Empty(OperationalHealthService.ProblemsIn(Drained, archive: null, TimeSpan.FromMinutes(2), Now));

    [Fact]
    public async Task AnOutboxThatIsDrainingIsHealthy()
    {
        await using (var db = NewDb())
        {
            var now = DateTime.UtcNow;
            db.OutboxMessages.AddRange(
                Message(now.AddMinutes(-10), dispatchedAtUtc: now.AddMinutes(-10)),
                // Seconds old: a burst being worked through, not a stall.
                Message(now.AddSeconds(-5)));
            await db.SaveChangesAsync();
        }

        await using var check = NewDb();
        var health = await Service(check).CheckAsync();

        Assert.Equal(1, health.Outbox.Pending);
        Assert.NotNull(health.Outbox.LastDispatchedAtUtc);
        Assert.DoesNotContain(health.Problems, problem => problem.Contains("outbox"));
    }

    /// <summary>
    /// The Relay stopped, or cannot reach Kafka. Registrations still succeed,
    /// so nothing else would say so: the dashboard just stops moving.
    /// </summary>
    [Fact]
    public async Task AnOutboxThatStoppedDrainingIsDegraded()
    {
        await using (var db = NewDb())
        {
            db.OutboxMessages.AddRange(
                Message(DateTime.UtcNow.AddMinutes(-30), error: "Broker transport failure"),
                Message(DateTime.UtcNow.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        await using var check = NewDb();
        var health = await Service(check).CheckAsync();

        Assert.Equal("degraded", health.Status);
        Assert.Equal(2, health.Outbox.Pending);
        Assert.Equal(1, health.Outbox.PendingWithErrors);
        Assert.Contains(health.Problems, problem => problem.Contains("waiting in the outbox"));
    }

    /// <summary>
    /// SQLite has no WAL archive. The answer is "not applicable" (null), never
    /// a row of zeros, which would read as "archiving works and has archived
    /// nothing".
    /// </summary>
    [SqliteFact]
    public async Task WithoutAWalArchiveTheArchiveIsNotReportedAsZero()
    {
        await using var db = NewDb();
        var health = await Service(db).CheckAsync();

        Assert.Null(health.WalArchive);
        Assert.Equal("ok", health.Status);
    }

    /// <summary>
    /// What pg_stat_archiver says, and the verdict drawn from it. Checked
    /// against the server's own view rather than an assumed configuration: CI's
    /// Postgres may run without archiving, the compose one runs with it, and
    /// either way the report has to match the server.
    /// </summary>
    [PostgresFact]
    public async Task TheArchiveIsReportedAsPostgresSeesIt()
    {
        await using var db = NewDb();
        var health = await Service(db).CheckAsync();

        Assert.NotNull(health.WalArchive);

        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('archive_mode') <> 'off', failed_count FROM pg_stat_archiver", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        var enabled = reader.GetBoolean(0);
        Assert.Equal(enabled, health.WalArchive!.Enabled);
        Assert.Equal(reader.GetInt64(1), health.WalArchive.FailedCount);

        // Off is a problem in its own right: it is the silent case -- no
        // recovery point, and nothing failing to say so.
        Assert.Equal(!enabled, health.Problems.Any(problem => problem.Contains("WAL archiving is off")));
    }
}
