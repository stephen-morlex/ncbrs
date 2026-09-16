namespace NCBRS.Models;

/// <summary>One revoked certificate, as the published list carries it.</summary>
public record RevocationEntry(
    string SerialHash,
    RevocationReason Reason,
    DateTime RevokedAtUtc
);

/// <summary>
/// The signed certificate revocation list.
///
/// Signed as a whole, because an unsigned list is worse than none: anyone
/// able to intercept it could strip the entry for the certificate they are
/// presenting, and a verifier would have no way to tell.
/// </summary>
public record CertificateRevocationList(
    string Issuer,
    string Version,
    string KeyId,

    /// <summary>
    /// The window this list covers. A full list starts at null; a delta
    /// starts where the caller's cached copy ended, and both bounds are
    /// inside the signature so a delta cannot be passed off as complete.
    /// </summary>
    DateTime? CoversFromUtc,

    DateTime IssuedAtUtc,

    /// <summary>
    /// When this list stops being trustworthy.
    ///
    /// The single most important field here. Without it, an offline verifier
    /// holding a months-old copy reads "not in my list" as "valid" -- which
    /// is exactly the certificate a forger wants presented. Past this
    /// moment a verifier must answer "unknown", not "valid".
    /// </summary>
    DateTime NextUpdateUtc,

    int Count,
    IReadOnlyList<RevocationEntry> Entries,
    string Signature
);

/// <summary>
/// Everything a device needs to verify certificates with no connection,
/// in one fetch.
///
/// Bundled rather than left as two calls because they have to be consistent:
/// a device that got the list but not the key, or a key rotated since the
/// list it holds, cannot verify anything -- and would discover that only
/// once it was already offline.
/// </summary>
public record OfflineVerificationBundle(
    /// <summary>The key currently signing.</summary>
    string KeyId,

    string Algorithm,

    /// <summary>The current key's public certificate.</summary>
    string PublicKeyPem,

    CertificateRevocationList Revocations,

    /// <summary>
    /// Every key a verifier should accept: the current one plus each retired
    /// key still verifying documents in circulation.
    ///
    /// This is what makes a rotation survivable for a device that has not
    /// synced since. Provisioned with only the current key, it would reject
    /// every certificate signed before the rotation -- years of genuine
    /// documents -- and be unable to tell that from a forgery.
    /// </summary>
    IReadOnlyList<VerificationKeyResponse> Keys
);
