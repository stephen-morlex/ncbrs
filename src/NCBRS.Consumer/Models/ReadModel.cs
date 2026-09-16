namespace NCBRS.Consumer.Models;

/// <summary>
/// One event this consumer has already applied.
///
/// Delivery is at-least-once from two independent directions -- a relay that
/// published but crashed before marking the row dispatched, and Kafka
/// redelivering on rebalance or deliberate replay -- so the same event will
/// arrive twice and that is normal, not a fault.
///
/// The projection below is built to be idempotent by construction, so this
/// ledger is not what keeps totals right. It is here for the things that
/// cannot be made idempotent by shape: the outbound pushes in WS-E, where
/// applying an event twice means telling the National ID Authority about a
/// birth twice. Those need to ask "have I already done this", and this is
/// what they ask.
/// </summary>
public class ProcessedEvent
{
    /// <summary>The outbox row id, carried in the ncbrs-event-id header.</summary>
    public Guid EventId { get; set; }

    public required string Topic { get; set; }

    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// How many times this event has arrived. Not needed to stay correct --
    /// it is the number that shows replay is happening at all, which is
    /// otherwise invisible until something is already wrong.
    /// </summary>
    public int Deliveries { get; set; } = 1;
}

/// <summary>
/// A correction or annulment that arrived before the registration it
/// describes, held until that registration turns up.
///
/// Kafka orders messages within a partition, not across topics, and
/// registrations, amendments and annulments are three topics. In steady
/// state this never shows: a correction is filed days after the birth. It
/// shows on a replay from the earliest offset -- which is exactly what this
/// consumer does on a rebuild -- where the whole of .amended can be
/// delivered before the whole of .registered.
///
/// Discarding those would make the projection depend on arrival order: the
/// same events, replayed, would leave an annulled birth counted as a live
/// one. Holding them is what makes a rebuild produce the same answer as the
/// original run.
///
/// A row that is never claimed is a registration this consumer genuinely
/// never received. It stays visible rather than being swept up, because
/// that is a gap someone needs to know about.
/// </summary>
public class PendingEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The registration this is waiting for.</summary>
    public required string Brn { get; set; }

    public required string Topic { get; set; }

    public required string Payload { get; set; }

    /// <summary>
    /// When the event happened, taken from the payload rather than from
    /// arrival. Held events are applied in this order, so two corrections to
    /// one record land the right way round however they were delivered.
    /// </summary>
    public DateTime OccurredAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One registered birth, as the reporting side sees it.
///
/// Facts keyed by BRN rather than counters, and that is the whole trick. A
/// counter is inherently unsafe under at-least-once delivery: "add one" done
/// twice is wrong, and no amount of care around it makes replay harmless. A
/// row keyed by the thing it describes can be written any number of times
/// and still says what it said -- so totals are derived by querying these,
/// never by incrementing anything.
///
/// It lives in the consumer's own database, not the registry's. Draft 6.4.1
/// is explicit that the API remains the single writer to the registry and
/// events are an outbound notification of what already happened; a consumer
/// writing back into the system of record would invert that.
/// </summary>
public class RegistrationFact
{
    public required string Brn { get; set; }

    public Guid BirthRecordId { get; set; }

    public required string DistrictId { get; set; }

    public Guid FacilityId { get; set; }

    public DateTime DateOfBirth { get; set; }

    public required string Sex { get; set; }

    /// <summary>
    /// When the centre published the registration, which is when it reached
    /// the centre -- not when the device registered it. A post offline for
    /// three weeks registers a birth long before this moment, so this is a
    /// reporting timestamp and never a legal one.
    ///
    /// **Named for what it is.** It was called `RegisteredAtUtc` while being
    /// the publish time, next to a `RegisteredAtUtc` on `BirthRecord` that is
    /// the device's -- one name for two instants that can be three weeks
    /// apart. The registry has already lost a timestamp to that ambiguity
    /// once.
    /// </summary>
    public DateTime PublishedAtUtc { get; set; }

    /// <summary>
    /// When the birth was registered on the device.
    ///
    /// The difference between this and <see cref="PublishedAtUtc"/> is how
    /// long the record waited for a link, and the difference between this and
    /// <see cref="DateOfBirth"/> is how long the family waited to reach a
    /// registrar. Those are questions about connectivity and about outreach
    /// respectively, and a single figure spanning both answers neither.
    ///
    /// Null on events published before the field existed. Excluded from the
    /// medians and counted, never treated as a zero-day lag.
    /// </summary>
    public DateTime? RegisteredAtUtc { get; set; }

    /// <summary>Hospital, Clinic or VillageHealthPost; null on events predating the field.</summary>
    public string? FacilityTier { get; set; }

    /// <summary>LiveBirth or FetalDeath; null on events predating the field.</summary>
    public string? VitalEventType { get; set; }

    /// <summary>
    /// Whether the registration fell inside the statutory window, decided at
    /// registration. Null means the event could not say -- which is a third
    /// answer, not a quiet "yes".
    /// </summary>
    public bool? WithinStatutoryWindow { get; set; }

    /// <summary>
    /// When the BRN was reconciled against the granted block. Null while the
    /// record is provisional, which is why it is the endpoint of
    /// time-to-registration rather than <see cref="RegisteredAtUtc"/>.
    /// </summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    /// <summary>Set when a correction arrives, so stale copies are visible as such.</summary>
    public DateTime? AmendedAtUtc { get; set; }

    /// <summary>
    /// Set when the registration is voided. The row is kept rather than
    /// deleted -- a district's history should not silently change shape --
    /// but every aggregate excludes it, because it describes no birth.
    /// </summary>
    public DateTime? AnnulledAtUtc { get; set; }

    public bool CertificateRevoked { get; set; }
}

/// <summary>
/// A death within 28 days of birth (WHO ICD-PM), keyed by the BRN it belongs
/// to. Perinatal and neonatal mortality are among the headline figures the
/// registry exists to produce.
///
/// Its own row rather than fields on the registration, exactly as design
/// decision #3 keeps them two records: a live birth followed by a death is
/// two vital events, and collapsing them here would reintroduce downstream
/// the conflation the register refuses.
/// </summary>
public class NeonatalOutcomeFact
{
    public required string Brn { get; set; }

    public Guid FacilityId { get; set; }

    public DateTime DeathDateUtc { get; set; }

    /// <summary>Antepartum, Intrapartum or Neonatal (WHO ICD-PM timing).</summary>
    public required string IcdPmTiming { get; set; }

    public required string IcdPmCauseCode { get; set; }

    public int DaysAfterBirth { get; set; }

    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>
/// A maternal death within 42 days (WHO ICD-MM), keyed by the BRN of the
/// birth. The maternal mortality ratio is reported internationally and is
/// derived from these.
/// </summary>
public class MaternalOutcomeFact
{
    public required string Brn { get; set; }

    public Guid FacilityId { get; set; }

    public DateTime DeathDateUtc { get; set; }

    public required string IcdMmCauseCode { get; set; }

    public int DaysAfterBirth { get; set; }

    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>
/// One sync batch a device sent, keyed by the batch id.
///
/// This is the only evidence the centre has of the offline tier working at
/// all. Sync reliability is a §10 indicator, and "which devices have gone
/// quiet" is the operational question behind it: a village post that has
/// stopped syncing is not a post with no births, it is a post whose births
/// nobody has.
/// </summary>
public class SyncBatchFact
{
    public Guid SyncBatchId { get; set; }

    public required string DeviceId { get; set; }

    public Guid FacilityId { get; set; }

    public required string DistrictId { get; set; }

    public int Submitted { get; set; }

    public int Registered { get; set; }

    /// <summary>
    /// Records the centre had already seen. The duplicate rate is a §10
    /// indicator, and this is the only place duplicates are currently
    /// reported into the stream at all.
    /// </summary>
    public int Duplicates { get; set; }

    public int Rejected { get; set; }

    public required string Status { get; set; }

    public DateTime SyncedAtUtc { get; set; }
}
