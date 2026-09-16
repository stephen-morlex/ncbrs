namespace NCBRS.Models;

/// <summary>
/// Body for issuing or reprinting. Only the device is supplied -- the
/// certificate's contents come from the registered birth, never from the
/// caller, so a certificate can never assert facts the registry does not
/// hold.
/// </summary>
public record IssueCertificateRequest
{
    public string DeviceId { get; init; } = string.Empty;
}

public record CertificateResponse(
    Guid CertificateId,
    string Brn,
    string ChildFullName,
    DateTime DateOfBirth,
    Sex Sex,
    string FacilityName,
    DateTime IssueDateUtc,

    /// <summary>
    /// Printed on the certificate as a QR code. Self-contained: it carries
    /// both the facts and the signature, so a verifier with the public key
    /// can check it with no network.
    /// </summary>
    string QrPayload,

    string Signature,
    int ReprintCount
);

/// <summary>What a scanner sends back to have a certificate checked.</summary>
public record VerifyCertificateRequest
{
    public string QrPayload { get; init; } = string.Empty;
}

/// <summary>
/// The result of checking a scanned certificate.
///
/// The facts returned come from the signed payload itself, never from a
/// lookup: this endpoint is reachable without a token so anyone holding a
/// certificate can check it, and answering from the registry would turn it
/// into a way to enumerate citizens' birth records.
///
/// The one thing the payload cannot answer is whether the document was
/// later withdrawn, so that single question is checked against the
/// revocation list. It enumerates nothing: the lookup key is a digest of
/// the signature the caller already holds, and without a valid signature
/// there is no key to ask with.
/// </summary>
public record VerifyCertificateResponse(
    /// <summary>
    /// Whether the certificate may be accepted. False for a forgery and
    /// false for a withdrawn document alike -- a verifier reading only this
    /// field still refuses both, which is why the revoked case narrows it
    /// rather than living solely in <see cref="Revoked"/>.
    /// </summary>
    bool Valid,
    string? Brn = null,
    string? ChildFullName = null,
    DateOnly? DateOfBirth = null,
    string? Sex = null,
    DateTime? IssueDateUtc = null,
    string? Reason = null,

    /// <summary>
    /// True when the signature held but the certificate has been withdrawn.
    /// Distinguished from a forgery because the holder did nothing wrong and
    /// should be told to collect a replacement, not turned away as a fraud.
    /// </summary>
    bool Revoked = false,

    RevocationReason? RevocationReason = null,
    DateTime? RevokedAtUtc = null
);

/// <summary>One public key a verifier should accept.</summary>
public record VerificationKeyResponse(
    string KeyId,
    string PublicKeyPem,

    /// <summary>True for the key currently signing; false for a retired one.</summary>
    bool Active
);

public record SigningKeyResponse(
    /// <summary>The key currently signing.</summary>
    string KeyId,

    string Algorithm,

    /// <summary>The current key's public certificate.</summary>
    string CertificatePem,

    /// <summary>
    /// Every key a verifier should accept, current and retired.
    ///
    /// A verifier provisioned with only the current key rejects every
    /// certificate signed before the last rotation — genuine documents,
    /// failing identically to forgeries. Take the whole set.
    /// </summary>
    IReadOnlyList<VerificationKeyResponse> Keys
);
