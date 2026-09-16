using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace NCBRS.Kafka;

public record DispatchResult(bool Delivered, string? Error = null);

/// <summary>
/// Produces outbox messages to Kafka.
///
/// Unlike the old request-path publisher this AWAITS the broker's delivery
/// report. It runs on the relay's background thread where latency costs
/// nobody anything, and the outbox needs a definite answer: a message may
/// only be marked dispatched once the broker has actually acknowledged it.
/// </summary>
public class KafkaOutboxTransport : IAsyncDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaOutboxTransport> _logger;

    public KafkaOutboxTransport(IOptions<KafkaOptions> options, ILogger<KafkaOutboxTransport> logger)
    {
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,

            // A civil registry: durability over latency. Nothing here is on a
            // user's critical path any more, so there is no reason to accept
            // weaker acknowledgement.
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 5,

            // Shorter than the default five minutes so a failed attempt comes
            // back promptly and the relay can retry it on its own schedule
            // rather than blocking a batch.
            MessageTimeoutMs = 30_000
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => KafkaLogging.HandleError(_logger, error, "outbox-producer"))
            .SetLogHandler((_, message) => KafkaLogging.HandleLog(_logger, message, "outbox-producer"))
            .Build();
    }

    /// <param name="eventId">
    /// The outbox row id, carried as a header so a consumer can recognise the
    /// same event arriving twice.
    ///
    /// Delivery is at-least-once in two independent places: a relay that
    /// published but crashed before marking the row dispatched will publish
    /// again, and Kafka itself redelivers on rebalance or deliberate replay.
    /// Neither is a fault to be engineered away -- replay is what the draft
    /// keeps 30-90 days of retention for -- so consumers need a stable
    /// identity to deduplicate on, and this is it.
    /// </param>
    public async Task<DispatchResult> DispatchAsync(
        string topic,
        string? partitionKey,
        string payload,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = new Message<string, string>
            {
                Key = partitionKey ?? string.Empty,
                Value = payload,
                Headers = [new Header(KafkaHeaders.EventId, Encoding.UTF8.GetBytes(eventId.ToString()))]
            };

            await _producer.ProduceAsync(topic, message, cancellationToken);

            return new DispatchResult(true);
        }
        catch (ProduceException<string, string> ex)
        {
            return new DispatchResult(false, ex.Error.Reason);
        }
        catch (KafkaException ex)
        {
            return new DispatchResult(false, ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        // Give anything still in flight a chance to land before shutdown.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Header names shared by the relay and every consumer. One definition, so
/// producer and consumer cannot disagree about the spelling and silently
/// lose the idempotency key.
/// </summary>
public static class KafkaHeaders
{
    public const string EventId = "ncbrs-event-id";
}
