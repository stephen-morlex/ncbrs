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

    private static bool IsExpectedWhileOffline(Error error)
        => error.Code is ErrorCode.Local_AllBrokersDown
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
