namespace NCBRS.Models;

/// <summary>
/// Highest level of education completed, aligned to UNESCO's ISCED 2011
/// levels rather than years of schooling.
///
/// Coded, and levels rather than years, because years are not comparable:
/// six years of schooling means different things in different systems, and
/// "how many mothers completed secondary education" is the question the
/// statistics are actually asked. NotStated is a real answer and is kept
/// distinct from null, which means the question was never put.
/// </summary>
public enum EducationLevel
{
    /// <summary>No formal schooling completed (ISCED 0 or below).</summary>
    None,

    /// <summary>Primary (ISCED 1).</summary>
    Primary,

    /// <summary>Lower secondary (ISCED 2).</summary>
    LowerSecondary,

    /// <summary>Upper secondary (ISCED 3).</summary>
    UpperSecondary,

    /// <summary>Post-secondary, non-tertiary (ISCED 4).</summary>
    PostSecondaryNonTertiary,

    /// <summary>Tertiary (ISCED 5-8).</summary>
    Tertiary,

    /// <summary>Asked, and the respondent did not or could not say.</summary>
    NotStated
}

/// <summary>
/// Occupation as an ISCO-08 major group.
///
/// Free text here would be worthless at national scale: "farmer",
/// "subsistence farming", "farms cassava" and "agriculture" would all be
/// different answers to the same question, and nothing could aggregate them.
/// Major groups are the coarsest ISCO level, which is what a facility
/// registrar can realistically assign without occupational coding training.
/// </summary>
public enum OccupationGroup
{
    /// <summary>ISCO-08 major group 1.</summary>
    Managers,

    /// <summary>ISCO-08 major group 2.</summary>
    Professionals,

    /// <summary>ISCO-08 major group 3.</summary>
    TechniciansAndAssociateProfessionals,

    /// <summary>ISCO-08 major group 4.</summary>
    ClericalSupportWorkers,

    /// <summary>ISCO-08 major group 5.</summary>
    ServiceAndSalesWorkers,

    /// <summary>ISCO-08 major group 6.</summary>
    SkilledAgriculturalForestryAndFishery,

    /// <summary>ISCO-08 major group 7.</summary>
    CraftAndRelatedTrades,

    /// <summary>ISCO-08 major group 8.</summary>
    PlantAndMachineOperators,

    /// <summary>ISCO-08 major group 9.</summary>
    ElementaryOccupations,

    /// <summary>ISCO-08 major group 0.</summary>
    ArmedForces,

    /// <summary>Not in the labour force -- student, homemaker, retired.</summary>
    NotEconomicallyActive,

    /// <summary>Asked, and the respondent did not or could not say.</summary>
    NotStated
}

/// <summary>
/// Statistical variables collected alongside the legal birth registration,
/// per the UN's Principles and Recommendations for a Vital Statistics System
/// (Rev. 3).
///
/// Kept in a separate table because, legally, the birth registration and the
/// statistical questionnaire are distinct functions -- NCBRS just captures
/// both in one facility workflow for efficiency. That separation is not
/// cosmetic and must hold in behaviour too: nothing here may gate a
/// certificate, block a registration, or appear on the printed document. A
/// birth whose questionnaire is never completed is a fully registered birth.
///
/// Every field is coded or numeric. None is free text, because the entire
/// purpose of this table is aggregation at national scale, and prose
/// aggregates into nothing.
/// </summary>
public class MaternalStatistics
{
    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    public EducationLevel? MotherEducationLevel { get; set; }
    public OccupationGroup? MotherOccupation { get; set; }

    public EducationLevel? FatherEducationLevel { get; set; }
    public OccupationGroup? FatherOccupation { get; set; }

    /// <summary>
    /// Previous live births to this mother, excluding this one. Together with
    /// PriorFetalDeaths this is gravidity/parity, which drives a large share
    /// of maternal risk stratification.
    /// </summary>
    public int PriorLiveBirths { get; set; }

    public int PriorFetalDeaths { get; set; }

    /// <summary>
    /// Antenatal contacts during this pregnancy. WHO's 2016 model recommends
    /// a minimum of eight; the count is what makes coverage against that
    /// measurable by region.
    /// </summary>
    public int? PrenatalVisitCount { get; set; }

    /// <summary>
    /// When antenatal care began. Late booking is a distinct risk factor from
    /// few visits, so the two are recorded separately.
    /// </summary>
    public DateOnly? MedicalCareBeganDate { get; set; }

    /// <summary>
    /// The mother's most recent previous live birth. Must be null when
    /// PriorLiveBirths is zero -- and the interval from it to this birth is a
    /// WHO indicator in its own right, short spacing being a known risk to
    /// both mother and infant.
    /// </summary>
    public DateOnly? DateOfLastLiveBirth { get; set; }

    public Guid RecordedByRegistrarId { get; set; }
    public Registrar? RecordedByRegistrar { get; set; }

    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set when the questionnaire is revised. Revision needs no approval
    /// workflow: this is not the legal record, and a corrected statistic is
    /// simply a better statistic.
    /// </summary>
    public DateTime? UpdatedAtUtc { get; set; }

    public Guid? TransactionId { get; set; }
}
