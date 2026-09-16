namespace NCBRS.Models;

/// <summary>
/// WHO/UN vital-event standard: a record is created for any infant showing
/// signs of life at birth (LiveBirth), regardless of gestational age.
/// A FetalDeath (stillbirth) is registered separately and never receives
/// a birth certificate. See NCBRS draft Section 6.5.1.
/// </summary>
public enum VitalEventType
{
    LiveBirth,
    FetalDeath
}

public enum Sex
{
    Male,
    Female,
    Undetermined
}

public enum BirthPlurality
{
    Singleton,
    Twin,
    Triplet,
    HigherOrderMultiple
}

public enum RecordStatus
{
    /// <summary>Issued offline from a device's reserved BRN block; not yet reconciled centrally.</summary>
    Provisional,
    /// <summary>Reconciled against the central registry after sync.</summary>
    Confirmed,
    /// <summary>A correction/amendment has been applied after initial confirmation.</summary>
    Amended,

    /// <summary>
    /// Voided: the registration should never have existed. Terminal — the row
    /// and its BRN are kept forever so the number still resolves to an
    /// explanation, but the record describes no living legal identity.
    /// </summary>
    Annulled
}

public class BirthRecord
{
    public Guid BirthRecordId { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The permanent Birth Registration Number, allocated from the facility's
    /// offline block (Facility.BrnBlockNextAvailable) at the moment of
    /// registration -- never reassigned later, even after central sync.
    /// </summary>
    public required string Brn { get; set; }

    public VitalEventType VitalEventType { get; set; }

    public Guid ChildPersonId { get; set; }
    public Person? ChildPerson { get; set; }

    public Guid? MotherPersonId { get; set; }
    public Person? MotherPerson { get; set; }

    public Guid? FatherPersonId { get; set; }
    public Person? FatherPerson { get; set; }

    public Guid FacilityId { get; set; }
    public Facility? Facility { get; set; }

    public Guid RegisteredByRegistrarId { get; set; }
    public Registrar? RegisteredByRegistrar { get; set; }

    public DateTime DateOfBirth { get; set; }

    public Sex Sex { get; set; }

    public int? BirthWeightGrams { get; set; }

    public decimal? GestationalAgeWeeks { get; set; }

    public BirthPlurality Plurality { get; set; }

    /// <summary>
    /// For multiples: which sibling this is (1, 2, 3...). Each sibling born
    /// alive gets its own BirthRecord; siblings not born alive are separate
    /// FetalDeath records -- never bundled into one row. Section 6.5.1.
    /// </summary>
    public int? BirthOrder { get; set; }

    /// <summary>
    /// Set when a reviewer confirms this record duplicates another. The row
    /// is never deleted -- the registry is append-only for legal reasons, and
    /// a certificate issued against this BRN must stay explicable.
    /// </summary>
    public Guid? SupersededByBirthRecordId { get; set; }

    public RecordStatus Status { get; set; } = RecordStatus.Provisional;

    /// <summary>
    /// When the central registry reconciled this record and confirmed its BRN
    /// as permanent (draft 5.1, 6.3). Null while the record is still
    /// provisional.
    ///
    /// Held separately from <see cref="Status"/> because the two answer
    /// different questions and a single enum cannot carry both: a record that
    /// is later amended becomes Amended, which would otherwise erase the fact
    /// that it had been confirmed.
    /// </summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    /// <summary>
    /// The fallback identifier this record arrived under, when the device had
    /// exhausted its BRN block offline (draft 6.3).
    ///
    /// Retained forever, even after a real BRN is assigned. A family may be
    /// holding a provisional slip carrying this value, and a clerk handed it
    /// months later has to be able to find the record — so lookup resolves on
    /// this as well as on the BRN.
    /// </summary>
    public string? ProvisionalIdentifier { get; set; }

    /// <summary>
    /// When a real BRN was assigned in place of the provisional identifier.
    /// Null while the record still carries one, which is what the
    /// reconciliation queue is read on.
    /// </summary>
    public DateTime? ReconciledAtUtc { get; set; }

    /// <summary>
    /// The batch that carried this record in from a device, when it came by
    /// sync rather than being filed directly against the central API. Null
    /// for an online registration, which is already at the centre.
    /// </summary>
    public Guid? ConfirmedBySyncBatchId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation to the two possible follow-on outcomes. Both are nullable
    // relationships kept in their own tables (see NeonatalOutcome /
    // MaternalOutcome) rather than columns here, since the large majority
    // of records have neither.
    public NeonatalOutcome? NeonatalOutcome { get; set; }
    public MaternalOutcome? MaternalOutcome { get; set; }
    public MaternalStatistics? MaternalStatistics { get; set; }

    /// <summary>
    /// Present only when the birth was registered outside the statutory
    /// window. Null for the large majority of records, which is why it is a
    /// separate table rather than columns here.
    /// </summary>
    public LateRegistration? LateRegistration { get; set; }

    /// <summary>
    /// Set when the registration has been voided. Checked wherever a record
    /// is acted on -- an annulled record cannot be certified, corrected, or
    /// given an outcome, and must not be matched against for duplicates.
    /// </summary>
    public RecordAnnulment? Annulment { get; set; }

    /// <summary>
    /// Denormalised from <see cref="Annulment"/> so the common check costs no
    /// join. A record is annulled if and only if this is set.
    /// </summary>
    public DateTime? AnnulledAtUtc { get; set; }

    /// <summary>
    /// A record can accumulate certificates over time: an amendment withdraws
    /// the one it contradicts, and a replacement is issued. At most one is
    /// valid at any moment, which the database enforces rather than trusts.
    /// </summary>
    public ICollection<Certificate> Certificates { get; set; } = [];

    /// <summary>
    /// Every correction made to this record, one row per field changed.
    /// </summary>
    public ICollection<BirthRecordAmendment> Amendments { get; set; } = [];
}
