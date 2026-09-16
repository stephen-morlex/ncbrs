namespace NCBRS.Models;

/// <summary>
/// Maternal death linked to a birth event, classified per WHO's ICD-MM
/// (the maternal-mortality classification system ICD-PM is modeled on).
/// </summary>
public class MaternalOutcome
{
    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    public DateTime DeathDateUtc { get; set; }

    public required string IcdMmCauseCode { get; set; }

    public Guid RecordedByRegistrarId { get; set; }
    public Registrar? RecordedByRegistrar { get; set; }
}
