namespace NCBRS.Models;

/// <summary>
/// Why a registration is being voided.
///
/// Coded, like every other classification here, because "how many
/// registrations were annulled as fraudulent this year" is a question the
/// Ministry has to be able to answer, and prose in a note field answers it
/// for nobody.
/// </summary>
public enum AnnulmentReason
{
    /// <summary>
    /// Filed against the wrong child, or entered during training and never
    /// meant to reach the register. A mistake, not a deception.
    /// </summary>
    RegisteredInError,

    /// <summary>
    /// Deliberately false: a birth that did not happen, or an identity
    /// manufactured for someone who already has one.
    /// </summary>
    FraudulentRegistration,

    /// <summary>Voided by judicial direction.</summary>
    CourtOrdered
}

/// <summary>
/// A registration voided because it should never have existed.
///
/// Distinct from an amendment, which says the register described a real
/// birth slightly wrongly. This says there was no such birth to describe --
/// a different act, needing different authority and a different downstream
/// instruction. A consumer told "amended" updates its copy; a consumer told
/// "annulled" must void it, and conflating the two would leave a National ID
/// record standing for an identity the register has withdrawn.
///
/// Also distinct from duplicate supersession, which resolves two records for
/// one child. There, one registration survives and the child keeps a legal
/// identity. Here nothing survives.
///
/// The record itself is never deleted, and its BRN is never returned to the
/// pool. A number that has been in circulation -- printed on a certificate,
/// quoted in a school register -- has to keep resolving to something that
/// explains what happened to it, forever.
/// </summary>
public class RecordAnnulment
{
    public Guid RecordAnnulmentId { get; set; } = Guid.CreateVersion7();

    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    public AnnulmentReason Reason { get; set; }

    /// <summary>
    /// Why this particular registration is being voided. Required, and held
    /// to a higher bar than an amendment's reason: this is the entire
    /// justification for withdrawing someone's legal identity.
    /// </summary>
    public required string Justification { get; set; }

    /// <summary>
    /// The court order, ministerial direction or investigation reference
    /// authorising this. Free text because it points at a physical document.
    /// Required for a court-ordered annulment.
    /// </summary>
    public string? AuthorityReference { get; set; }

    public Guid AnnulledByRegistrarId { get; set; }
    public Registrar? AnnulledByRegistrar { get; set; }

    public DateTime AnnulledAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True when a valid certificate existed and was revoked by this.</summary>
    public bool CertificateRevoked { get; set; }

    public Guid? TransactionId { get; set; }
}
