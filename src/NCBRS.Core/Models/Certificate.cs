namespace NCBRS.Models;

/// <summary>
/// Issued only for LiveBirth records -- fetal deaths never receive a birth
/// certificate (Section 6.5.1 of the NCBRS draft).
/// </summary>
public class Certificate
{
    public Guid CertificateId { get; set; } = Guid.CreateVersion7();

    public Guid BirthRecordId { get; set; }
    public BirthRecord? BirthRecord { get; set; }

    public DateTime IssueDateUtc { get; set; }

    /// <summary>
    /// Digital signature over the certificate payload, produced with the
    /// Ministry's signing key (Section 6.7). Lets any third party verify
    /// authenticity offline via the printed QR code.
    /// </summary>
    public required string SignatureHash { get; set; }

    public required string QrPayload { get; set; }

    public int ReprintCount { get; set; }

    /// <summary>
    /// Set when an amendment changed a field this certificate's signature
    /// covers, so the document no longer describes the register.
    ///
    /// The row is withdrawn rather than deleted. That a certificate was
    /// issued and later withdrawn is itself a fact about the record, and the
    /// printed copy may still be in a family's hands -- deleting the row
    /// would leave nothing to explain it by.
    /// </summary>
    public DateTime? WithdrawnAtUtc { get; set; }

    public string? WithdrawnReason { get; set; }

    public bool IsValid => WithdrawnAtUtc is null;
}
