using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using NCBRS.Consumer.Services;

namespace NCBRS.Kafka;

/// <summary>
/// Builds the reporting projection from the event stream (draft 6.4.1).
///
/// Deployed on its own so it can scale and restart independently of the
/// registration API, and so a slow projection can never delay a birth being
/// registered.
/// </summary>
public class BirthRecordDashboardConsumer(
    IServiceScopeFactory scopes,
    IOptions<KafkaOptions> options,
    ILogger<BirthRecordDashboardConsumer> logger) : BackgroundService
{
    private readonly KafkaOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroupId,

            // Replay from the start on first run -- see the retention note in
            // 6.4.1. Safe precisely because the projection is idempotent: a
            // full replay rebuilds the same numbers.
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // Offsets are committed after the projection is written, never on
            // a timer. Auto-commit would acknowledge messages this consumer
            // had not yet applied, so a crash in that window would LOSE them
            // -- and idempotency is no defence against loss, only against
            // duplication. At-least-once is the behaviour to want here, and
            // this is what produces it.
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(config)
            .SetErrorHandler((_, error) => KafkaLogging.HandleError(logger, error, "consumer"))
            .SetLogHandler((_, message) => KafkaLogging.HandleLog(logger, message, "consumer"))
            .Build();

        string[] topics =
        [
            _options.BirthRecordsRegisteredTopic,
            _options.BirthRecordsAmendedTopic,
            _options.BirthRecordsAnnulledTopic,
            _options.NeonatalOutcomesTopic,
            _options.MaternalOutcomesTopic,
            _options.SyncAuditTopic
        ];

        consumer.Subscribe(topics);

        logger.LogInformation("Dashboard consumer subscribed to {Topics}", string.Join(", ", topics));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(stoppingToken);

                    if (result?.Message is null)
                    {
                        continue;
                    }

                    await HandleAsync(result, stoppingToken);

                    // Only now. Everything above has been written and
                    // committed to the projection's own store.
                    //
                    // Committed directly, not stored first: disabling
                    // auto-commit disables the offset store with it, so
                    // StoreOffset throws here.
                    consumer.Commit(result);
                }
                catch (ConsumeException ex)
                {
                    // Same demotion as the native callbacks: a broker that
                    // isn't reachable is the expected offline state here,
                    // not an incident worth an error every loop.
                    KafkaLogging.HandleError(logger, ex.Error, "consumer");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Deliberately not committed. The message will be
                    // redelivered, which is the whole reason the projection
                    // has to tolerate seeing it twice.
                    logger.LogError(ex, "Failed to project an event; it will be redelivered");

                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal on shutdown
        }
        finally
        {
            // Leaves the group cleanly rather than waiting for the session
            // to time out, so a restart resumes from the last commit
            // instead of the partition being reassigned mid-flight.
            consumer.Close();
        }
    }

    private async Task HandleAsync(ConsumeResult<Ignore, string> result, CancellationToken cancellationToken)
    {
        var eventId = EventId(result.Message.Headers);

        using var scope = scopes.CreateScope();
        var projector = scope.ServiceProvider.GetRequiredService<BirthRecordProjector>();

        var projection = await projector.ApplyAsync(
            result.Topic, eventId, result.Message.Value, cancellationToken);

        switch (projection.Outcome)
        {
            case ProjectionOutcome.Applied:
                logger.LogInformation("Projected {Topic} (event {EventId})", result.Topic, eventId);
                break;

            case ProjectionOutcome.AlreadySeen:
                logger.LogDebug("Skipped duplicate: {Detail}", projection.Detail);
                break;

            case ProjectionOutcome.Deferred:
                logger.LogInformation("Held out of order: {Detail}", projection.Detail);
                break;

            case ProjectionOutcome.Ignored:
                logger.LogDebug("{Detail}", projection.Detail);
                break;
        }
    }

    private static Guid? EventId(Headers? headers)
    {
        if (headers is null
            || !headers.TryGetLastBytes(KafkaHeaders.EventId, out var raw)
            || !Guid.TryParse(Encoding.UTF8.GetString(raw), out var id))
        {
            return null;
        }

        return id;
    }
}
