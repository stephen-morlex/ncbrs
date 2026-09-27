using Microsoft.Extensions.Configuration;
using NCBRS.Data;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The registry's own connection, held to the rule the other links follow
/// (<see cref="TransportSecurityTests"/>, <see cref="KafkaTransportSecurityTests"/>):
/// outside Development it is Postgres, and Postgres verifies the server.
///
/// Before this the base settings named SQLite, so a deployment that forgot
/// the override ran the central registry on one file; and Npgsql's default
/// SSL mode, Prefer, never checks who it is talking to.
/// </summary>
public class DatabaseTransportSecurityTests
{
    private static IConfiguration Settings(string? provider, string? connectionString)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                ["ConnectionStrings:Default"] = connectionString,
            })
            .Build();

    private static string? Refusal(string? provider, string? connectionString, bool isDevelopment = false)
        => NcbrsDatabase.RefusalOutsideDevelopment(Settings(provider, connectionString), isDevelopment);

    [Theory]
    [InlineData("Sqlite")]
    [InlineData(null)]
    public void SqliteIsRefusedOutsideDevelopment(string? provider)
    {
        Assert.Contains("not Postgres", Refusal(provider, "Data Source=ncbrs.db"));
        Assert.Null(Refusal(provider, "Data Source=ncbrs.db", isDevelopment: true));
    }

    /// <summary>
    /// Prefer (Npgsql's default) and Require both encrypt without checking
    /// the server, so neither stops someone impersonating it.
    /// </summary>
    [Theory]
    [InlineData("Host=db.ncbrs.ss;Database=ncbrs;Username=ncbrs_app")]
    [InlineData("Host=db.ncbrs.ss;Database=ncbrs;Username=ncbrs_app;SSL Mode=Prefer")]
    [InlineData("Host=db.ncbrs.ss;Database=ncbrs;Username=ncbrs_app;SSL Mode=Require")]
    [InlineData("Host=db.ncbrs.ss;Database=ncbrs;Username=ncbrs_app;SSL Mode=Disable")]
    public void AnUnverifiedRemoteConnectionIsRefused(string connectionString)
    {
        Assert.Contains("does not verify", Refusal("Postgres", connectionString));
        Assert.Null(Refusal("Postgres", connectionString, isDevelopment: true));
    }

    [Theory]
    [InlineData("Host=db.ncbrs.ss;Database=ncbrs;Username=ncbrs_app;SSL Mode=VerifyFull")]
    [InlineData("Host=db.ncbrs.ss;Database=ncbrs;Username=ncbrs_app;SSL Mode=VerifyCA;Root Certificate=/etc/ncbrs/pg-ca.pem")]
    public void AVerifiedConnectionStarts(string connectionString)
        => Assert.Null(Refusal("Postgres", connectionString));

    /// <summary>No network path, nothing to protect.</summary>
    [Theory]
    [InlineData("Host=/var/run/postgresql;Database=ncbrs;Username=ncbrs_app")]
    [InlineData("Host=localhost;Port=5433;Database=ncbrs;Username=ncbrs_app")]
    [InlineData("Host=LOCALHOST:5433;Database=ncbrs;Username=ncbrs_app")]
    [InlineData("Host=127.0.0.1;Database=ncbrs;Username=ncbrs_app")]
    [InlineData("Host=[::1]:5432;Database=ncbrs;Username=ncbrs_app")]
    public void ASocketOrLoopbackConnectionIsExempt(string connectionString)
        => Assert.Null(Refusal("Postgres", connectionString));

    /// <summary>Every host in a failover list must be local for the exemption to hold.</summary>
    [Fact]
    public void OneRemoteHostInAListIsEnoughToRequireVerification()
        => Assert.NotNull(Refusal("Postgres", "Host=localhost,db-replica.ncbrs.ss;Database=ncbrs;Username=ncbrs_app"));

    // --- what ships --------------------------------------------------------------------------

    /// <summary>
    /// The base file is loaded in every environment. It names no provider
    /// (the refusal then says Postgres is required) and no connection string,
    /// which carries credentials and belongs in the environment or a secret
    /// store.
    /// </summary>
    [Theory]
    [InlineData("NCBRS.Api")]
    [InlineData("NCBRS.Relay")]
    public void TheBaseSettingsNameNoDatabase(string project)
    {
        var settings = ShippedSettings.Read(project, "appsettings.json");

        Assert.False(ShippedSettings.Sets(settings, "Database", "Provider", out _));
        Assert.False(ShippedSettings.Sets(settings, "ConnectionStrings", "Default", out _));
    }
}
