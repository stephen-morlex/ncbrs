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
}
