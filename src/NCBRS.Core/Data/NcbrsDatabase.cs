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

    /// <summary>
    /// Why this registry connection must not run, or null. Outside
    /// Development, refused rather than warned about, like plain-HTTP Keycloak
    /// and a plaintext broker:
    ///
    /// - **SQLite** is the dev and test provider. The base settings used to
    ///   name it, so a deployment that forgot the override would have run the
    ///   central registry on a single file, with no sign anything was wrong.
    /// - **Postgres must verify the server's certificate**
    ///   (<c>SSL Mode=VerifyFull</c> or <c>VerifyCA</c>). Npgsql's default,
    ///   <c>Prefer</c>, encrypts only when the server offers to and never checks
    ///   who it is talking to, so anyone on the path can downgrade the link or
    ///   impersonate the server and read the whole register. <c>Require</c>
    ///   encrypts but still does not check. A Unix socket or loopback host has
    ///   no network path to protect and is exempt.
    /// </summary>
    public static string? RefusalOutsideDevelopment(
        IConfiguration configuration, bool isDevelopment, string connectionStringName = "Default")
    {
        if (isDevelopment)
        {
            return null;
        }

        if (ProviderFrom(configuration) != DatabaseProvider.Postgres)
        {
            return "Database:Provider is not Postgres outside Development. SQLite is the development and test "
                   + "provider; the central registry runs on Postgres. Set Database__Provider=Postgres.";
        }

        var connectionString = configuration.GetConnectionString(connectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null; // Configure refuses a missing connection string with its own message.
        }

        var settings = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (settings.SslMode is Npgsql.SslMode.VerifyFull or Npgsql.SslMode.VerifyCA || IsLocal(settings.Host))
        {
            return null;
        }

        return $"The registry connection uses SSL Mode={settings.SslMode} outside Development, which does not "
               + "verify the database server: anyone on the path could impersonate it and read or alter the "
               + "register. Set SSL Mode=VerifyFull (with Root Certificate if the CA is not in the system store).";
    }

    private static bool IsLocal(string? hosts)
        => !string.IsNullOrWhiteSpace(hosts)
           && hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .All(host => host.StartsWith('/')
                            || HostName(host).ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]");

    private static string HostName(string host)
        => host.StartsWith('[')
            ? host[..(host.IndexOf(']') + 1)]
            : host.Count(c => c == ':') == 1 ? host[..host.IndexOf(':')] : host;

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
