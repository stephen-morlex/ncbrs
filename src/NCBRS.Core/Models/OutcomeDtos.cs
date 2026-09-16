namespace NCBRS.Models;

/// <summary>
/// Records a death within 28 days of a live birth. Per WHO/UN standards the
/// live birth and the death are two separate records -- this never edits the
/// BirthRecord, it attaches to it.
/// </summary>
public record RecordNeonatalOutcomeRequest
{
    public DateTime DeathDateUtc { get; init; }

    /// <summary>
    /// WHO ICD-PM timing. For a live birth that later dies the timing is
    /// Neonatal by definition; Antepartum and Intrapartum are stillbirth
    /// categories and belong to a FetalDeath record.
    /// </summary>
    public IcdPmTiming IcdPmTiming { get; init; } = IcdPmTiming.Neonatal;

    /// <summary>Coded, never free text -- this is what makes national statistics comparable.</summary>
    public string IcdPmCauseCode { get; init; } = string.Empty;

    /// <summary>ICD-PM requires the contributing maternal condition to be recorded alongside the cause.</summary>
    public string? ContributingMaternalConditionCode { get; init; }

    public string DeviceId { get; init; } = string.Empty;
}

/// <summary>
/// Records a maternal death linked to a birth event, classified per WHO
/// ICD-MM. Applies whether the birth was live or a fetal death -- the
/// mother's outcome is independent of the child's.
/// </summary>
public record RecordMaternalOutcomeRequest
{
    public DateTime DeathDateUtc { get; init; }

    public string IcdMmCauseCode { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;
}

public record NeonatalOutcomeResponse(
    Guid BirthRecordId,
    string Brn,
    DateTime DeathDateUtc,
    IcdPmTiming IcdPmTiming,
    string IcdPmCauseCode,
    string? ContributingMaternalConditionCode,
    int DaysAfterBirth
);

public record MaternalOutcomeResponse(
    Guid BirthRecordId,
    string Brn,
    DateTime DeathDateUtc,
    string IcdMmCauseCode,
    int DaysAfterBirth
);
