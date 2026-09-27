using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace NCBRS.Kafka;

public class KafkaOptions
{
    public const string SectionName = "Kafka";

    public required string BootstrapServers { get; set; }

    public string BirthRecordsRegisteredTopic { get; set; } = "ncbrs.birth-records.registered";

    public string NeonatalOutcomesTopic { get; set; } = "ncbrs.outcomes.neonatal";

    public string MaternalOutcomesTopic { get; set; } = "ncbrs.outcomes.maternal";

    public string BirthRecordsAmendedTopic { get; set; } = "ncbrs.birth-records.amended";

    /// <summary>
    /// Registrations voided outright. Separate from .amended because the two
    /// carry opposite instructions to a consumer: update your copy, versus
    /// void it. An addition to the topic list in draft 6.4.1.
    /// </summary>
    public string BirthRecordsAnnulledTopic { get; set; } = "ncbrs.birth-records.annulled";

    public string SyncAuditTopic { get; set; } = "ncbrs.sync.audit";

    /// <summary>Consumer group id for the in-process downstream consumer demo.</summary>
    public string ConsumerGroupId { get; set; } = "ncbrs-dashboard-updater";

    /// <summary>
    /// How clients reach the broker. **SASL over TLS unless Development turns
    /// it off** (the compose broker is plaintext).
    ///
    /// The events carry BRNs, sex, county, coded causes of death and the
    /// before-and-after values of corrections, names included. Over plaintext
    /// anyone on the path reads them; with no authentication anyone who can
    /// reach the broker can also *publish* into topics the reporting
    /// projection trusts, and that WS-E will relay to the National ID
    /// Authority. Before this there was no way to configure either.
    /// </summary>
    public SecurityProtocol SecurityProtocol { get; set; } = SecurityProtocol.SaslSsl;

    public SaslMechanism SaslMechanism { get; set; } = SaslMechanism.ScramSha512;

    public string? SaslUsername { get; set; }

    /// <summary>
    /// From the environment or a secret store, never appsettings.json — these
    /// credentials can write into the event stream.
    /// </summary>
    public string? SaslPassword { get; set; }

    /// <summary>The CA that signed the broker's certificate, when not in the system store.</summary>
    public string? SslCaLocation { get; set; }

    /// <summary>A client certificate and key, for mutual TLS instead of SASL.</summary>
    public string? SslCertificateLocation { get; set; }

    public string? SslKeyLocation { get; set; }

    public string? SslKeyPassword { get; set; }

    /// <summary>
    /// Applies the connection settings to a producer or consumer config, so
    /// both sides of the stream are configured by one piece of code and
    /// cannot drift.
    /// </summary>
    public void ApplyTo(ClientConfig config)
    {
        config.BootstrapServers = BootstrapServers;
        config.SecurityProtocol = SecurityProtocol;

        if (SecurityProtocol is SecurityProtocol.SaslSsl or SecurityProtocol.SaslPlaintext)
        {
            config.SaslMechanism = SaslMechanism;
            config.SaslUsername = SaslUsername;
            config.SaslPassword = SaslPassword;
        }

        if (SecurityProtocol is SecurityProtocol.Ssl or SecurityProtocol.SaslSsl)
        {
            config.SslCaLocation = SslCaLocation;
            config.SslCertificateLocation = SslCertificateLocation;
            config.SslKeyLocation = SslKeyLocation;
            config.SslKeyPassword = SslKeyPassword;
        }
    }

    /// <summary>
    /// Why this configuration must not run, or null. Outside Development the
    /// link must be encrypted **and** the client authenticated: SASL over TLS
    /// with a username, or TLS with a client certificate. Encryption alone
    /// still lets anyone who reaches the broker publish forged events.
    /// Refused at startup, like plain-HTTP Keycloak (<c>KeycloakOptions</c>).
    /// </summary>
    public string? RefusalOutsideDevelopment(bool isDevelopment)
    {
        if (isDevelopment)
        {
            return null;
        }

        return SecurityProtocol switch
        {
            SecurityProtocol.SaslSsl when !string.IsNullOrWhiteSpace(SaslUsername) => null,
            SecurityProtocol.SaslSsl =>
                "Kafka:SaslUsername is not set. Supply the broker credentials (Kafka__SaslUsername, Kafka__SaslPassword) from the environment or a secret store.",
            SecurityProtocol.Ssl when !string.IsNullOrWhiteSpace(SslCertificateLocation) => null,
            SecurityProtocol.Ssl =>
                "Kafka:SecurityProtocol is Ssl without a client certificate (Kafka:SslCertificateLocation): the link is encrypted but anyone who reaches the broker can publish into the event stream.",
            _ =>
                $"Kafka:SecurityProtocol is {SecurityProtocol} outside Development. The events carry birth, death-cause and correction data, and an unauthenticated or unencrypted broker lets anyone on the path read them or publish forged ones. Use SaslSsl, or Ssl with a client certificate.",
        };
    }
}

/// <summary>
/// Applies <see cref="KafkaOptions.RefusalOutsideDevelopment"/> when the host
/// starts (registered with <c>ValidateOnStart</c>), so a misconfigured Relay or
/// Consumer stops before it connects rather than on its first message.
/// </summary>
public sealed class KafkaOptionsValidator(bool isDevelopment) : IValidateOptions<KafkaOptions>
{
    public ValidateOptionsResult Validate(string? name, KafkaOptions options)
        => options.RefusalOutsideDevelopment(isDevelopment) is { } refusal
            ? ValidateOptionsResult.Fail(refusal)
            : ValidateOptionsResult.Success;
}
