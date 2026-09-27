using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using Npgsql;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// A fact that only means something against Postgres: roles and grants do not
/// exist in SQLite. Skipped with a reason there rather than passing silently,
/// because a green test on SQLite would claim a proof it never made.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (TestDatabase.Provider != DatabaseProvider.Postgres)
        {
            Skip = "Postgres only (roles and grants). Run with NCBRS_TEST_PROVIDER=Postgres, as CI's Postgres job does.";
        }
    }
}

/// <summary>
/// Plan §17 item 16: "the app role provably lacks the verbs". The deployment
/// step is <c>deploy/postgres/app-role-grants.sql</c>. This runs that exact
/// script against a migrated database and then acts as the role it grants.
///
/// Every refusal is asserted as <c>42501 insufficient_privilege</c>, and that
/// is the point. The append-only trigger would refuse an UPDATE as well, with a
/// different error, so a test that only checked "the UPDATE failed" would pass
/// with the grants missing entirely, for the wrong reason.
/// </summary>
public class AuditLogLeastPrivilegeTests
{
    private const string InsufficientPrivilege = "42501";

    private static string GrantScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NCBRS.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "deploy", "postgres", "app-role-grants.sql"));
    }

    /// <summary>
    /// The script as psql would run it: variables substituted, psql's own
    /// backslash commands (not SQL) left to psql.
    /// </summary>
    private static string AsSql(string script, string role)
        => string.Join('\n', script
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith('\\'))
            .Select(line => line.Replace(":\"app_role\"", $"\"{role}\"")));

    private static async Task<PostgresException> RefusedAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private static async Task RunAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task TheAppRoleCanAppendToTheAuditTrailButNeverRewriteIt()
    {
        using var database = TestDatabase.Create();
        var role = $"ncbrs_app_t_{Guid.NewGuid():N}"[..28];

        string connectionString;
        await using (var db = new NcbrsDbContext(database.Options))
        {
            // One row to try to rewrite, written the way the application writes one.
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "BirthRecord",
                EntityId = "100001",
                Action = "Create",
                DeviceId = "TABLET-07",
                CountyCode = "SS0101",
            });
            await db.SaveChangesAsync();
            connectionString = db.Database.GetConnectionString()!;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await RunAsync(connection, $"""CREATE ROLE "{role}" NOLOGIN;""");
        try
        {
            // The deployment step, exactly as written.
            await RunAsync(connection, AsSql(GrantScript(), role));
            await RunAsync(connection, $"""SET ROLE "{role}";""");

            // It is a working application role: it reads, and it appends.
            await RunAsync(connection, """SELECT count(*) FROM "AuditLogs";""");
            await RunAsync(connection, """
                INSERT INTO "AuditLogs"
                SELECT (json_populate_record(null::"AuditLogs",
                        (to_jsonb(a) || jsonb_build_object('AuditLogId', gen_random_uuid()))::json)).*
                FROM "AuditLogs" a LIMIT 1;
                """);

            // And it writes the rest of the register freely.
            await RunAsync(connection, """UPDATE "Facilities" SET "Name" = "Name";""");

            // But it cannot rewrite, delete or empty the audit trail...
            Assert.Equal(InsufficientPrivilege, (await RefusedAsync(connection,
                """UPDATE "AuditLogs" SET "Action" = 'Rewritten';""")).SqlState);
            Assert.Equal(InsufficientPrivilege, (await RefusedAsync(connection,
                """DELETE FROM "AuditLogs";""")).SqlState);
            Assert.Equal(InsufficientPrivilege, (await RefusedAsync(connection,
                """TRUNCATE "AuditLogs";""")).SqlState);

            // ...nor switch off the trigger that would stop it: only the table's
            // owner can, which is why the application must never own the tables.
            Assert.Equal(InsufficientPrivilege, (await RefusedAsync(connection,
                """ALTER TABLE "AuditLogs" DISABLE TRIGGER USER;""")).SqlState);

            // ...nor touch the migration history.
            Assert.Equal(InsufficientPrivilege, (await RefusedAsync(connection,
                """DELETE FROM "__EFMigrationsHistory";""")).SqlState);
        }
        finally
        {
            await RunAsync(connection, "RESET ROLE;");
            await RunAsync(connection, $"""DROP OWNED BY "{role}"; DROP ROLE "{role}";""");
        }
    }

    /// <summary>
    /// Running the deployment step twice -- after every migration, as it must
    /// be -- leaves exactly the same privileges.
    /// </summary>
    [PostgresFact]
    public async Task TheScriptIsIdempotent()
    {
        using var database = TestDatabase.Create();
        var role = $"ncbrs_app_t_{Guid.NewGuid():N}"[..28];

        string connectionString;
        await using (var db = new NcbrsDbContext(database.Options))
        {
            connectionString = db.Database.GetConnectionString()!;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await RunAsync(connection, $"""CREATE ROLE "{role}" NOLOGIN;""");
        try
        {
            var sql = AsSql(GrantScript(), role);
            await RunAsync(connection, sql);
            await RunAsync(connection, sql);

            await using var check = new NpgsqlCommand(
                $"""SELECT has_table_privilege('{role}', '"AuditLogs"', 'UPDATE') OR has_table_privilege('{role}', '"AuditLogs"', 'DELETE');""",
                connection);
            Assert.False((bool)(await check.ExecuteScalarAsync())!);
        }
        finally
        {
            await RunAsync(connection, $"""DROP OWNED BY "{role}"; DROP ROLE "{role}";""");
        }
    }
}
