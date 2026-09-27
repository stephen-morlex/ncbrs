using System.Text.Json;
using Confluent.Kafka;
using NCBRS.Kafka;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The broker link carries BRNs, sex, county, coded causes of death and the
/// before-and-after values of corrections. Before this there was no way to
/// configure TLS or authentication at all: every deployment would have run
/// plaintext and unauthenticated, so anyone on the path could read the stream
/// and anyone who reached the broker could publish into it.
///
/// Same shape as <see cref="TransportSecurityTests"/>: secure by default, the
/// relaxation in Development settings only, refused anywhere else.
/// </summary>
public class KafkaTransportSecurityTests
{
    private static KafkaOptions Options(SecurityProtocol protocol, string? username = null, string? certificate = null)
        => new()
        {
            BootstrapServers = "broker:9093",
            SecurityProtocol = protocol,
            SaslUsername = username,
            SslCertificateLocation = certificate,
        };

    [Fact]
    public void TheDefaultIsSaslOverTls()
        => Assert.Equal(SecurityProtocol.SaslSsl, new KafkaOptions { BootstrapServers = "broker:9093" }.SecurityProtocol);

    [Theory]
    [InlineData(SecurityProtocol.Plaintext)]
    [InlineData(SecurityProtocol.SaslPlaintext)]
    public void AnUnencryptedLinkIsRefusedOutsideDevelopment(SecurityProtocol protocol)
    {
        Assert.NotNull(Options(protocol, username: "relay").RefusalOutsideDevelopment(isDevelopment: false));
        Assert.Null(Options(protocol).RefusalOutsideDevelopment(isDevelopment: true));
    }

    /// <summary>
    /// Encryption alone is not enough: without authentication anyone who can
    /// reach the broker can publish forged events into the stream.
    /// </summary>
    [Fact]
    public void AnEncryptedButAnonymousLinkIsRefused()
    {
        Assert.NotNull(Options(SecurityProtocol.Ssl).RefusalOutsideDevelopment(isDevelopment: false));
        Assert.NotNull(Options(SecurityProtocol.SaslSsl).RefusalOutsideDevelopment(isDevelopment: false));
    }

    [Fact]
    public void AnEncryptedAuthenticatedLinkStarts()
    {
        Assert.Null(Options(SecurityProtocol.SaslSsl, username: "relay").RefusalOutsideDevelopment(isDevelopment: false));
        Assert.Null(Options(SecurityProtocol.Ssl, certificate: "/etc/ncbrs/relay.pem").RefusalOutsideDevelopment(isDevelopment: false));
    }

    [Fact]
    public void TheValidatorFailsWithTheReason()
    {
        var result = new KafkaOptionsValidator(isDevelopment: false).Validate(null, Options(SecurityProtocol.Plaintext));

        Assert.True(result.Failed);
        Assert.Contains("Plaintext", result.FailureMessage);
        Assert.True(new KafkaOptionsValidator(isDevelopment: true).Validate(null, Options(SecurityProtocol.Plaintext)).Succeeded);
    }

    /// <summary>
    /// Producer and consumer are configured by the same method, so the two
    /// ends of the stream cannot disagree about how to reach the broker.
    /// </summary>
    [Fact]
    public void BothClientsGetTheConnectionSettings()
    {
        var options = new KafkaOptions
        {
            BootstrapServers = "broker:9093",
            SaslUsername = "relay",
            SaslPassword = "from-the-secret-store",
            SslCaLocation = "/etc/ncbrs/ca.pem",
        };

        ClientConfig[] configs = [new ProducerConfig(), new ConsumerConfig()];
        foreach (var config in configs)
        {
            options.ApplyTo(config);

            Assert.Equal("broker:9093", config.BootstrapServers);
            Assert.Equal(SecurityProtocol.SaslSsl, config.SecurityProtocol);
            Assert.Equal(SaslMechanism.ScramSha512, config.SaslMechanism);
            Assert.Equal("relay", config.SaslUsername);
            Assert.Equal("from-the-secret-store", config.SaslPassword);
            Assert.Equal("/etc/ncbrs/ca.pem", config.SslCaLocation);
        }
    }

    /// <summary>
    /// Observed live: SASL over TLS against a plaintext listener fails with a
    /// Local_Transport error, the same code as a broker that is down, which
    /// is logged at Debug for an offline-first deployment. A refused handshake
    /// or refused credentials will never succeed on retry and must surface.
    /// </summary>
    [Theory]
    [InlineData(ErrorCode.Local_Transport, "sasl_ssl://localhost:9092/bootstrap: SSL handshake failed: Disconnected: connecting to a PLAINTEXT broker listener?", true)]
    [InlineData(ErrorCode.Local_Authentication, "SASL SCRAM-SHA-512 mechanism handshake failed", true)]
    [InlineData(ErrorCode.SaslAuthenticationFailed, "Authentication failed", true)]
    [InlineData(ErrorCode.Local_Transport, "localhost:9092/bootstrap: Connect to ipv4#127.0.0.1:9092 failed: Connection refused", false)]
    [InlineData(ErrorCode.Local_AllBrokersDown, "1/1 brokers are down", false)]
    public void ARefusedHandshakeIsAConfigurationFaultNotAnOutage(ErrorCode code, string reason, bool fault)
        => Assert.Equal(fault, KafkaLogging.IsConfigurationFault(new Error(code, reason)));

    // --- what ships --------------------------------------------------------------------------

    private static bool Relaxed(JsonElement settings)
        => ShippedSettings.Sets(settings, "Kafka", "SecurityProtocol", out var protocol)
           && protocol.GetString() is { } value
           && (value.Equals("Plaintext", StringComparison.OrdinalIgnoreCase)
               || value.Equals("SaslPlaintext", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("NCBRS.Relay")]
    [InlineData("NCBRS.Consumer")]
    [InlineData("NCBRS.Api")]
    public void NoBaseSettingsFileRelaxesTheBrokerLinkOrHoldsItsPassword(string project)
    {
        var settings = ShippedSettings.Read(project, "appsettings.json");

        Assert.False(Relaxed(settings));
        Assert.False(ShippedSettings.Sets(settings, "Kafka", "SaslPassword", out _));
    }

    /// <summary>
    /// The Relay had no launch profile, so <c>dotnet run</c> started it as
    /// Production. Harmless while nothing depended on the environment; with the
    /// refusal it would stop every developer's Relay against the plaintext
    /// compose broker.
    /// </summary>
    [Fact]
    public void TheRelayRunsAsDevelopmentLocally()
    {
        var profiles = ShippedSettings.Read("NCBRS.Relay", Path.Combine("Properties", "launchSettings.json"))
            .GetProperty("profiles");

        Assert.All(profiles.EnumerateObject(), profile => Assert.Equal(
            "Development",
            profile.Value.GetProperty("environmentVariables").GetProperty("DOTNET_ENVIRONMENT").GetString()));
    }
}
