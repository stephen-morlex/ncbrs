namespace NCBRS.Events;

/// <summary>
/// Published to "ncbrs.outcomes.neonatal". Perinatal mortality is one of the
/// headline indicators the registry exists to produce, so the statistics
/// tier consumes these rather than polling the database.
/// </summary>
public record NeonatalOutcomeRecordedEvent(
    string Brn,
    Guid BirthRecordId,
    Guid FacilityId,
    DateTime DeathDateUtc,
    string IcdPmTiming,
    string IcdPmCauseCode,
    int DaysAfterBirth,
    DateTime EventTimestampUtc,
    Guid? TransactionId
);

/// <summary>
/// Published to "ncbrs.outcomes.maternal". Maternal mortality ratio is
/// reported nationally and internationally, and is derived from these.
/// </summary>
public record MaternalOutcomeRecordedEvent(
    string Brn,
    Guid BirthRecordId,
    Guid FacilityId,
    DateTime DeathDateUtc,
    string IcdMmCauseCode,
    int DaysAfterBirth,
    DateTime EventTimestampUtc,
    Guid? TransactionId
);
