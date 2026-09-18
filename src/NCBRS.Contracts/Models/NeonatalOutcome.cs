namespace NCBRS.Models;

/// <summary>
/// WHO ICD-PM timing categories for a perinatal death.
/// </summary>
public enum IcdPmTiming
{
    Antepartum,
    Intrapartum,
    Neonatal
}

/// <summary>
/// Death within 28 days following a live birth. Always linked to an existing
/// BirthRecord -- a live birth and a subsequent death are two separate legal
/// records per WHO/UN standards, never one combined "outcome" field.
/// </summary>
public class NeonatalOutcome
{
    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    public DateTime DeathDateUtc { get; set; }

    public IcdPmTiming IcdPmTiming { get; set; }

    /// <summary>
    /// WHO ICD-PM cause code (fetal/neonatal cause), coded -- not free text --
    /// so cause-of-death statistics are comparable across facilities.
    /// </summary>
    public required string IcdPmCauseCode { get; set; }

    /// <summary>
    /// WHO ICD-PM's compulsory linkage to a contributing maternal condition.
    /// </summary>
    public string? ContributingMaternalConditionCode { get; set; }

    public Guid RecordedByRegistrarId { get; set; }
    public Registrar? RecordedByRegistrar { get; set; }
}
