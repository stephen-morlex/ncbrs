using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using Npgsql;

namespace NCBRS.Tests;

/// <summary>
/// A registry database for one test, on whichever provider the run targets
/// (WS-A1).
///
/// Set <c>NCBRS_TEST_PROVIDER=Postgres</c> to run the suite against the engine
/// the central tier actually uses. It defaults to SQLite because that is what
/// makes the suite finish in seconds, and a test suite people wait for is a
/// test suite people stop running.
///
/// Both providers give each test its own database, which is what keeps their
/// semantics identical — no shared state, no ordering between tests, and a
/// test that deliberately rolls a transaction back behaves the same on either.
///
/// **SQLite** opens a private in-memory database per test; creating one costs
/// almost nothing.
///
/// **Postgres** migrates a template once, then clones it per test. The clone
/// is a file copy (~80ms) rather than a replay of every migration (seconds),
/// which is the difference between a suite people run against Postgres and
/// one they do not.
///
/// The obvious alternative — one database with each test in a rolled-back
/// transaction — was tried and rejected: EF opens its own transaction for
/// SaveChanges, so an outer one collides with it, and suppressing that would
/// have quietly broken the tests that roll back on purpose.
///
/// Note the schemas are not quite identical: the SQLite path uses
/// EnsureCreated (fast, but it never runs a migration's SQL, so no triggers),
/// while Postgres is migrated and therefore always carries the append-only
/// audit triggers. Where a test needs the triggers on SQLite it asks for
/// <c>migrated: true</c>. Postgres being the stricter of the two is the
/// reason to run against it.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private const string ProviderVariable = "NCBRS_TEST_PROVIDER";

    private static readonly Lazy<string> PostgresTemplate = new(PreparePostgres, isThreadSafe: true);

    private readonly DbConnection? _connection;
    private readonly string? _postgresDatabase;

    public DbContextOptions<NcbrsDbContext> Options { get; }

    public static DatabaseProvider Provider =>
        Enum.TryParse<DatabaseProvider>(
            Environment.GetEnvironmentVariable(ProviderVariable), ignoreCase: true, out var provider)
            ? provider
            : DatabaseProvider.Sqlite;

    /// <summary>The connection string for the Postgres test server.</summary>
    private static string PostgresAdminConnectionString =>
        Environment.GetEnvironmentVariable("NCBRS_TEST_POSTGRES")
        ?? "Host=localhost;Port=5433;Database=postgres;Username=ncbrs;Password=ncbrs-dev";

    private TestDatabase(
        DbConnection? connection,
        string? postgresDatabase,
        DbContextOptions<NcbrsDbContext> options)
    {
        _connection = connection;
        _postgresDatabase = postgresDatabase;
        Options = options;
    }

    /// <param name="migrated">
    /// Apply migrations rather than EnsureCreated. Only meaningful on SQLite,
    /// where it is the difference between having the append-only audit
    /// triggers and not; the Postgres path is always migrated.
    /// </param>
    public static TestDatabase Create(bool migrated = false)
        => Provider == DatabaseProvider.Postgres ? CreatePostgres() : CreateSqlite(migrated);

    private static TestDatabase CreateSqlite(bool migrated)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<NcbrsDbContext>()
            .UseSqlite(connection)
            .Options;

        using var db = new NcbrsDbContext(options);

        if (migrated)
        {
            db.Database.Migrate();
        }
        else
        {
            db.Database.EnsureCreated();
        }

        return new TestDatabase(connection, postgresDatabase: null, options);
    }

    private static TestDatabase CreatePostgres()
    {
        var template = PostgresTemplate.Value;
        var databaseName = $"ncbrs_t_{Guid.NewGuid():N}";

        using (var admin = new NpgsqlConnection(AdminConnectionString()))
        {
            admin.Open();
            Execute(admin, $"""CREATE DATABASE "{databaseName}" TEMPLATE "{template}";""");
        }

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString())
        {
            Database = databaseName,

            // Unpooled, so nothing is still holding this database open when
            // the test finishes and it has to be dropped.
            Pooling = false
        };

        var options = new DbContextOptionsBuilder<NcbrsDbContext>()
            .UseNpgsql(builder.ConnectionString,
                npgsql => npgsql.MigrationsAssembly(NcbrsDatabase.PostgresMigrationsAssembly))
            .Options;

        return new TestDatabase(connection: null, databaseName, options);
    }

    /// <summary>
    /// Migrates one template database per run, to be cloned per test.
    ///
    /// Stale databases from previous runs are dropped first: a crashed run
    /// leaves them behind, and they accumulate silently until someone
    /// wonders why the server is full.
    /// </summary>
    private static string PreparePostgres()
    {
        var templateName = $"ncbrs_test_template_{Environment.ProcessId}";

        using (var admin = new NpgsqlConnection(AdminConnectionString()))
        {
            admin.Open();

            foreach (var stale in Query(admin,
                "SELECT datname FROM pg_database WHERE datname LIKE 'ncbrs_t\\_%' OR datname LIKE 'ncbrs_test%'"))
            {
                Execute(admin, $"""DROP DATABASE IF EXISTS "{stale}" WITH (FORCE);""");
            }

            Execute(admin, $"""CREATE DATABASE "{templateName}";""");
        }

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString())
        {
            Database = templateName,
            Pooling = false
        };

        var options = new DbContextOptionsBuilder<NcbrsDbContext>()
            .UseNpgsql(builder.ConnectionString,
                npgsql => npgsql.MigrationsAssembly(NcbrsDatabase.PostgresMigrationsAssembly))
            .Options;

        using (var db = new NcbrsDbContext(options))
        {
            db.Database.Migrate();
        }

        return templateName;
    }

    private static string AdminConnectionString() => PostgresAdminConnectionString;

    private static List<string> Query(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        var results = new List<string>();

        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection?.Dispose();

        if (_postgresDatabase is null)
        {
            return;
        }

        using var admin = new NpgsqlConnection(AdminConnectionString());
        admin.Open();

        Execute(admin, $"""DROP DATABASE IF EXISTS "{_postgresDatabase}" WITH (FORCE);""");
    }
}
