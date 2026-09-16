namespace NCBRS.Models;

/// <summary>
/// The statistical questionnaire (UN P&amp;R Rev. 3), captured with the
/// registration or completed later.
///
/// Every field is optional. This is not the legal record, and a
/// half-answered questionnaire is more useful than a refused one — a
/// registrar who cannot get an answer must be able to move on rather than
/// invent one.
/// </summary>
public record MaternalStatisticsRequest
{
    public EducationLevel? MotherEducationLevel { get; init; }
    public OccupationGroup? MotherOccupation { get; init; }

    public EducationLevel? FatherEducationLevel { get; init; }
    public OccupationGroup? FatherOccupation { get; init; }

    public int PriorLiveBirths { get; init; }
    public int PriorFetalDeaths { get; init; }

    public int? PrenatalVisitCount { get; init; }
    public DateOnly? MedicalCareBeganDate { get; init; }
    public DateOnly? DateOfLastLiveBirth { get; init; }
}

/// <summary>
/// As above, plus the device that captured it, for the standalone endpoint.
/// On the registration path the device is already named by the registration.
/// </summary>
public record CaptureMaternalStatisticsRequest : MaternalStatisticsRequest
{
    public string DeviceId { get; init; } = string.Empty;
}

public record MaternalStatisticsResponse(
    string Brn,
    EducationLevel? MotherEducationLevel,
    OccupationGroup? MotherOccupation,
    EducationLevel? FatherEducationLevel,
    OccupationGroup? FatherOccupation,
    int PriorLiveBirths,
    int PriorFetalDeaths,
    int? PrenatalVisitCount,
    DateOnly? MedicalCareBeganDate,
    DateOnly? DateOfLastLiveBirth,

    /// <summary>
    /// Months between the mother's last live birth and this one, derived
    /// rather than stored. A WHO indicator in its own right: spacing under 24
    /// months carries measurably higher risk to both mother and infant, so it
    /// is returned where the dates allow it rather than left for every
    /// consumer to recompute.
    /// </summary>
    int? BirthIntervalMonths,

    /// <summary>
    /// True when the antenatal contacts meet WHO's 2016 minimum of eight.
    /// Null when the count was not captured.
    /// </summary>
    bool? MeetsWhoAntenatalMinimum,

    DateTime RecordedAtUtc,
    DateTime? UpdatedAtUtc
);
