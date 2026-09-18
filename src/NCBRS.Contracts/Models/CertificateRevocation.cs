namespace NCBRS.Models;

/// <summary>
/// Why a certificate stopped being valid. Coded rather than free text, for
/// the same reason cause-of-death is (design decision #3): a verifier in
/// another system has to act on this, and prose it cannot parse is prose it
/// will ignore.
/// </summary>
public enum RevocationReason
{
    /// <summary>A field the signature covers was corrected.</summary>
    Amended,

    /// <summary>The record was ruled a duplicate of another registration.</summary>
    SupersededAsDuplicate,

    /// <summary>
    /// The registration itself was voided: there was no such birth to
    /// certify. Distinct from an amendment, which corrects a real one.
    /// </summary>
    RegistrationAnnulled
}

/// <summary>
/// One entry in the certificate revocation list.
///
/// A certificate already printed keeps verifying forever: the signature over
/// its contents is genuine, and nothing in the document itself can say it
/// was later withdrawn. The only way a verifier learns otherwise is by
/// checking a list published by the Ministry -- which is what this table
/// holds.
///
/// The row is separate from <see cref="Certificate.WithdrawnAtUtc"/> on
/// purpose. That column is internal state; this is a published fact, and it
/// must keep its own shape even if the certificate row is later reissued,
/// reprinted, or otherwise changed underneath it.
/// </summary>
public class CertificateRevocation
{
    public Guid CertificateRevocationId { get; set; } = Guid.CreateVersion7();

    public Guid CertificateId { get; set; }
    public Certificate? Certificate { get; set; }

    public Guid BirthRecordId { get; set; }

    /// <summary>
    /// The published identifier of the revoked document: an opaque digest of
    /// its signature.
    ///
    /// It is derived rather than stored on the certificate because the
    /// printed QR carries no serial number, and adding one would change the
    /// canonical signed payload -- which would stop every certificate
    /// already in a family's hands from verifying. A verifier holding the
    /// paper can recompute this from what is printed on it.
    ///
    /// Being a digest is also what makes the list publishable: it names no
    /// child, no BRN and no facility, so distributing it widely -- which a
    /// revocation list must be, to be worth anything -- leaks nothing.
    /// </summary>
    public required string SerialHash { get; set; }

    public RevocationReason Reason { get; set; }

    public DateTime RevokedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? RevokedByRegistrarId { get; set; }

    public Guid? TransactionId { get; set; }
}
