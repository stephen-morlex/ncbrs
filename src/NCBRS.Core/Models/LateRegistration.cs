namespace NCBRS.Models;

/// <summary>
/// What a late registration is being supported by.
///
/// Coded rather than free text, for the reason cause-of-death is (design
/// decision #3): the mix of evidence accepted for late registrations is a
/// statistic the Ministry needs -- if most late registrations nationally
/// rest on a sworn affidavit rather than a health record, that says
/// something about where the registration system is failing. Prose in a
/// notes field answers no such question.
/// </summary>
public enum LateRegistrationEvidenceType
{
    /// <summary>A facility record of the birth itself, made at the time.</summary>
    HealthFacilityRecord,

    /// <summary>An antenatal or delivery card held by the mother.</summary>
    AntenatalOrDeliveryCard,

    /// <summary>An immunisation record naming the child and date of birth.</summary>
    ImmunisationRecord,

    /// <summary>
    /// Written attestation by the traditional birth attendant present. Often
    /// the only contemporaneous evidence that exists for a home birth.
    /// </summary>
    BirthAttendantAttestation,

    /// <summary>A baptismal or other religious register entry.</summary>
    ReligiousRecord,

    /// <summary>A school enrolment record stating the date of birth.</summary>
    SchoolRecord,

    /// <summary>A sworn declaration by a parent or guardian.</summary>
    SwornAffidavit,

    /// <summary>A court order directing registration.</summary>
    CourtOrder
}

public enum LateRegistrationStatus
{
    /// <summary>Filed, awaiting verification by a district registrar.</summary>
    PendingApproval,

    /// <summary>Verified. The registration now carries full effect.</summary>
    Approved,

    /// <summary>
    /// Refused. The record is kept -- a refused late registration is itself
    /// evidence, and deleting it would let the same claim simply be filed
    /// again with nothing to show it had been seen before.
    /// </summary>
    Rejected
}

/// <summary>
/// A birth registered after the statutory window (draft Sections 4.1, 5.3).
///
/// Late registration is the ordinary route for a large share of rural births
/// -- a child registered when they first reach school is the rule, not the
/// exception -- so this must be a working process, not an obstacle. What it
/// adds is a second person checking evidence before the registration carries
/// full effect, because the alternative is that a date of birth can be
/// asserted years later with nothing to support it.
///
/// Backdating is the fraud this deters. A birth date decides school
/// entry, the age of majority, marriage eligibility and pension timing, so
/// there is real incentive to claim a different one, and by definition
/// nobody contemporaneous is there to contradict it.
/// </summary>
public class LateRegistration
{
    public Guid LateRegistrationId { get; set; } = Guid.CreateVersion7();

    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    /// <summary>
    /// Days between the birth and its capture on a device. Stored rather
    /// than recomputed: the statutory window is set in law and may change,
    /// and a record must stay explicable under the rule that applied when it
    /// was filed.
    /// </summary>
    public int DaysLate { get; set; }

    /// <summary>The window, in days, that was in force when this was filed.</summary>
    public int WindowDaysAtFiling { get; set; }

    public LateRegistrationEvidenceType EvidenceType { get; set; }

    /// <summary>
    /// Identifies the document produced -- a card number, register entry, or
    /// affidavit reference. Free text because it is a pointer to a physical
    /// document, not a statistic.
    /// </summary>
    public string? EvidenceReference { get; set; }

    /// <summary>Who presented the claim, and on what standing.</summary>
    public required string DeclarantName { get; set; }

    public required string DeclarantRelationship { get; set; }

    public LateRegistrationStatus Status { get; set; } = LateRegistrationStatus.PendingApproval;

    public Guid SubmittedByRegistrarId { get; set; }
    public Registrar? SubmittedByRegistrar { get; set; }

    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? ReviewedByRegistrarId { get; set; }
    public Registrar? ReviewedByRegistrar { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }

    public string? ReviewNote { get; set; }

    public Guid? TransactionId { get; set; }
}
