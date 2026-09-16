using System.Text.Json;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Events;
using NCBRS.Models;

namespace NCBRS.Kafka;

/// <summary>
/// Stages domain events for dispatch.
///
/// Implementations do no network I/O: they add a row to the caller's
/// DbContext, which the caller's own SaveChanges then commits alongside the
/// domain change. Enqueue therefore has to be called BEFORE SaveChanges, not
/// after -- that ordering is the entire mechanism.
/// </summary>
public interface IEventPublisher
{
    void EnqueueBirthRegistered(BirthRegisteredEvent evt, string districtId);

    void EnqueueNeonatalOutcome(NeonatalOutcomeRecordedEvent evt, string districtId);

    void EnqueueMaternalOutcome(MaternalOutcomeRecordedEvent evt, string districtId);

    void EnqueueBirthRecordAmended(BirthRecordAmendedEvent evt, string districtId);

    void EnqueueBirthRecordAnnulled(BirthRecordAnnulledEvent evt, string districtId);

    void EnqueueSyncBatchProcessed(SyncBatchProcessedEvent evt, string districtId);
}

/// <summary>
/// Writes events to the outbox table rather than to Kafka.
///
/// A registration can no longer be delayed or affected by a broker outage at
/// all: the request path never touches Kafka. That guarantee used to depend
/// on the publisher being carefully fire-and-forget; now it is structural.
/// </summary>
public class OutboxEventPublisher(NcbrsDbContext db, IOptions<KafkaOptions> options) : IEventPublisher
{
    private readonly KafkaOptions _options = options.Value;

    public void EnqueueBirthRegistered(BirthRegisteredEvent evt, string districtId)
        => Enqueue(_options.BirthRecordsRegisteredTopic, evt, districtId, evt.TransactionId);

    public void EnqueueNeonatalOutcome(NeonatalOutcomeRecordedEvent evt, string districtId)
        => Enqueue(_options.NeonatalOutcomesTopic, evt, districtId, evt.TransactionId);

    public void EnqueueMaternalOutcome(MaternalOutcomeRecordedEvent evt, string districtId)
        => Enqueue(_options.MaternalOutcomesTopic, evt, districtId, evt.TransactionId);

    public void EnqueueBirthRecordAmended(BirthRecordAmendedEvent evt, string districtId)
        => Enqueue(_options.BirthRecordsAmendedTopic, evt, districtId, evt.TransactionId);

    public void EnqueueBirthRecordAnnulled(BirthRecordAnnulledEvent evt, string districtId)
        => Enqueue(_options.BirthRecordsAnnulledTopic, evt, districtId, evt.TransactionId);

    public void EnqueueSyncBatchProcessed(SyncBatchProcessedEvent evt, string districtId)
        => Enqueue(_options.SyncAuditTopic, evt, districtId, evt.TransactionId);

    private void Enqueue<T>(string topic, T evt, string districtId, Guid? transactionId)
        => db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = topic,
            PartitionKey = districtId,
            Payload = JsonSerializer.Serialize(evt),
            TransactionId = transactionId
        });
}
