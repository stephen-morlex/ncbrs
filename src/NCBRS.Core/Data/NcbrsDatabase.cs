using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace NCBRS.Data;

public enum DatabaseProvider
{
    /// <summary>The central tier (draft 6.4, 7.1).</summary>
    Postgres,

    /// <summary>
    /// Development and tests only. Kept because the test suite runs hundreds
    /// of contexts against in-memory databases in a few seconds, which no
    /// container-backed engine matches -- but it is not what production runs,
    /// and nothing may rely on behaviour only it has.
    /// </summary>
    Sqlite
}

/// <summary>
/// Chooses the database provider in one place (WS-A1).
///
/// Four hosts open this context, and a provider chosen separately in each is
/// four chances to run the registration API against one engine and the relay
/// draining its outbox against another.
///
/// **Migrations are per provider, and that is not incidental.** EF bakes
/// provider-specific column types into a scaffolded migration -- SQLite's
/// "TEXT" where Postgres wants "text" or "uuid" -- so one history cannot
/// serve both. The Postgres set lives in its own assembly
/// (NCBRS.Migrations.Postgres); the SQLite set stays in Core where it was
/// written. Each is named explicitly below, so which history applies is a
/// fact in the code rather than a consequence of where a file happens to sit.
/// </summary>
public static class NcbrsDatabase
{
    public const string PostgresMigrationsAssembly = "NCBRS.Migrations.Postgres";

    /// <summary>
    /// Reads "Database:Provider". Absent means SQLite, so a developer who has
    /// not read this file still gets a working machine -- the opposite
    /// default would fail on a missing container.
    ///
    /// A value that is present but unrecognised is refused rather than
    /// defaulted. "Postgresql" or "postgress" silently falling back to SQLite
    /// would give a central tier a single-file database and no sign anything
    /// was wrong until it was holding a country's births.
    /// </summary>
    public static DatabaseProvider ProviderFrom(IConfiguration configuration)
    {
        var configured = configuration["Database:Provider"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return DatabaseProvider.Sqlite;
        }

        return Enum.TryParse<DatabaseProvider>(configured, ignoreCase: true, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"Database:Provider is '{configured}', which is not a provider this system has. "
                + $"Use one of: {string.Join(", ", Enum.GetNames<DatabaseProvider>())}.");
    }

    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder options,
        IConfiguration configuration,
        string connectionStringName = "Default")
    {
        var provider = ProviderFrom(configuration);
        var connectionString = configuration.GetConnectionString(connectionStringName);

        return provider switch
        {
            DatabaseProvider.Postgres => options.UseNpgsql(
                connectionString ?? throw new InvalidOperationException(
                    $"Connection string '{connectionStringName}' is required when the provider is Postgres. "
                    + "It carries credentials and must come from the environment or a secret store, "
                    + "never from appsettings.json."),
                npgsql => npgsql.MigrationsAssembly(PostgresMigrationsAssembly)),

            _ => options.UseSqlite(
                connectionString ?? "Data Source=ncbrs.db",
                sqlite => sqlite.MigrationsAssembly(typeof(NcbrsDbContext).Assembly.FullName))
        };
    }
}
