using Confluent.Kafka;

namespace NCBRS.Kafka;

/// <summary>
/// Routes librdkafka's native callbacks through ILogger instead of letting
/// them write straight to stderr, where they repeat every couple of seconds
/// and bury the application's own output.
///
/// Nothing is discarded -- an unreachable broker is demoted to Debug rather
/// than silenced, because for an offline-first deployment that is the
/// expected steady state (Section 6.4.1: the database is the system of
/// record, the topic is a notification). Genuine client faults keep their
/// severity.
/// </summary>
/// <remarks>
/// Public rather than internal because the relay and consumer are now
/// separate assemblies -- this used to live alongside its only callers.
/// </remarks>
public static class KafkaLogging
{
    public static void HandleError(ILogger logger, Error error, string clientRole)
    {
        var level = IsExpectedWhileOffline(error) ? LogLevel.Debug : LogLevel.Error;

        logger.Log(level, "Kafka {ClientRole}: {Reason} ({ErrorCode})",
            clientRole, error.Reason, error.Code);
    }

    /// <summary>
    /// librdkafka's own diagnostic stream. Debug-level here so it stays
    /// retrievable by raising the log level, without competing with request
    /// logs at default verbosity.
    /// </summary>
    public static void HandleLog(ILogger logger, LogMessage message, string clientRole)
    {
        logger.LogDebug("Kafka {ClientRole} [{Facility}]: {Message}",
            clientRole, message.Facility, message.Message);
    }

    /// <summary>
    /// A broker that answered and refused our TLS or credentials is not an
    /// outage: it is configuration that will never succeed on retry. librdkafka
    /// reports a failed TLS handshake as a plain transport error, so without
    /// this a misconfigured Relay would log at Debug, forever, looking exactly
    /// like a link that is down.
    /// </summary>
    public static bool IsConfigurationFault(Error error)
        => error.Code is ErrorCode.Local_Authentication
               or ErrorCode.SaslAuthenticationFailed
               or ErrorCode.Local_Ssl
           || error.Reason.Contains("SSL handshake failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedWhileOffline(Error error)
        => !IsConfigurationFault(error) && error.Code is ErrorCode.Local_AllBrokersDown
            or ErrorCode.Local_Transport
            or ErrorCode.Local_Resolve
            or ErrorCode.Local_TimedOut
            or ErrorCode.BrokerNotAvailable

            // A subscribed topic with no events yet. Topics are created by
            // the first publish, so the outcome topics genuinely do not
            // exist until somewhere in the country records a death -- which
            // is a quiet week, not a fault. Logging it as an error on every
            // poll teaches operators to ignore the error stream.
            or ErrorCode.UnknownTopicOrPart;
}
