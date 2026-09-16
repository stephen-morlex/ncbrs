using NCBRS.Events;
using NCBRS.Kafka;

namespace NCBRS.Tests;

/// <summary>
/// Records what was staged without touching a database, for tests that care
/// about the domain write rather than the outbox row.
/// </summary>
public class NoOpEventPublisher : IEventPublisher
{
    public List<(string Topic, string PartitionKey)> Enqueued { get; } = [];

    /// <summary>
    /// Kept as the event, for the same reason amendments and annulments are:
    /// a registration event carries the §10 indicator fields and the device's
    /// registration time, and one that dropped any of them would still reach
    /// the right topic while telling the projection nothing it can measure.
    /// </summary>
    public List<BirthRegisteredEvent> Registrations { get; } = [];

    public void EnqueueBirthRegistered(BirthRegisteredEvent evt, string districtId)
    {
        Registrations.Add(evt);
        Enqueued.Add(("birth-registered", districtId));
    }

    public void EnqueueNeonatalOutcome(NeonatalOutcomeRecordedEvent evt, string districtId)
        => Enqueued.Add(("neonatal-outcome", districtId));

    public void EnqueueMaternalOutcome(MaternalOutcomeRecordedEvent evt, string districtId)
        => Enqueued.Add(("maternal-outcome", districtId));

    /// <summary>
    /// Kept as the event itself, not just the topic: an amendment event that
    /// omitted a change or lost a previous value would still be published to
    /// the right topic, so the topic alone proves nothing worth asserting.
    /// </summary>
    public List<BirthRecordAmendedEvent> Amendments { get; } = [];

    public List<SyncBatchProcessedEvent> SyncBatches { get; } = [];

    public void EnqueueBirthRecordAmended(BirthRecordAmendedEvent evt, string districtId)
    {
        Amendments.Add(evt);
        Enqueued.Add(("birth-record-amended", districtId));
    }

    public void EnqueueSyncBatchProcessed(SyncBatchProcessedEvent evt, string districtId)
    {
        SyncBatches.Add(evt);
        Enqueued.Add(("sync-audit", districtId));
    }

    /// <summary>
    /// Kept as the event, not just the topic: an annulment instructs a
    /// consumer to void its copy, and an event missing its reason or its
    /// certificate flag would still reach the right topic while telling the
    /// consumer nothing it can act on.
    /// </summary>
    public List<BirthRecordAnnulledEvent> Annulments { get; } = [];

    public void EnqueueBirthRecordAnnulled(BirthRecordAnnulledEvent evt, string districtId)
    {
        Annulments.Add(evt);
        Enqueued.Add(("birth-record-annulled", districtId));
    }
}
