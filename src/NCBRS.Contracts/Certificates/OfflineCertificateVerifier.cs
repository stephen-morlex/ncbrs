using System.Security.Cryptography;
using System.Text;
using NCBRS.Models;

namespace NCBRS.Certificates;

public enum OfflineVerdict
{
    /// <summary>Signed by the Ministry and absent from a complete, in-date list.</summary>
    Valid,

    /// <summary>Signed, but the document has been withdrawn.</summary>
    Revoked,

    /// <summary>Not signed by the Ministry key, or altered since signing.</summary>
    NotGenuine,

    /// <summary>
    /// Genuine, but this device cannot say whether it still stands. The
    /// answer a stale or incomplete cache must give instead of "valid".
    /// </summary>
    Unknown
}

public record OfflineVerification(
    OfflineVerdict Verdict,
    string? Brn,
    string? ChildFullName,
    DateOnly? DateOfBirth,
    string? Sex,
    DateTime? IssueDateUtc,
    RevocationReason? RevocationReason,
    DateTime? RevokedAtUtc,
    string Detail
)
{
    /// <summary>
    /// Whether the certificate may be accepted. Only a positive answer
    /// qualifies -- Unknown reads as false, because a device that cannot
    /// check is in no position to approve.
    /// </summary>
    public bool Accept => Verdict is OfflineVerdict.Valid;
}

/// <summary>
/// Checks a certificate with no network at all.
///
/// This is the tier the whole system is built for (design decision #1): a
/// district office or village post that may not reach the registry for
/// days. It performs the same two checks the online endpoint does --
/// signature, then revocation -- against a public key and a cached list the
/// device was provisioned with.
///
/// The difference that matters is the third answer. Online, absence from
/// the list means not revoked. Offline it means that only while the cache is
/// complete and in date; otherwise the honest answer is Unknown, and a
/// verifier must send the holder to a connected office rather than accept a
/// certificate it could not actually check.
/// </summary>
public class OfflineCertificateVerifier(
    CertificatePayloadVerifier verifier,
    RevocationListCache cache)
{
    /// <summary>
    /// The identifier the published list names a revoked document by,
    /// recomputed from the signature printed on the certificate.
    ///
    /// Must stay identical to the recorder's derivation in the API; a
    /// mismatch would silently mean no revoked certificate is ever found,
    /// which fails open. The tests pin the two against each other.
    /// </summary>
    public static string SerialFor(string signature)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(signature));

        return Convert.ToBase64String(digest, 0, 16)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public OfflineVerification Verify(string? qrPayload)
    {
        var canonical = verifier.Verify(qrPayload);

        if (canonical is null)
        {
            // No detail about *why*: a tampered payload and a malformed one
            // are both simply not genuine, and saying which would help
            // someone iterate towards a forgery.
            return Answer(OfflineVerdict.NotGenuine, null,
                "This certificate could not be verified against the Ministry signing key.");
        }

        // Shape: NCBRS|v1|brn|childName|dob|sex|facilityId|issuedAt
        var fields = canonical.Split('|');

        if (fields.Length != 8)
        {
            return Answer(OfflineVerdict.NotGenuine, null,
                "The certificate payload is not in a recognised format.");
        }

        var serial = SerialFor(qrPayload!.Split('.')[^1]);
        var revocation = cache.Find(serial);

        if (revocation is not null)
        {
            // A revocation is positive knowledge: a stale cache can still be
            // trusted to have found something, only not to have found
            // everything. So this is answered even when coverage is not
            // Complete.
            return Answer(OfflineVerdict.Revoked, fields,
                $"This certificate was withdrawn on {revocation.RevokedAtUtc:yyyy-MM-dd} "
                + $"({Describe(revocation.Reason)}). A replacement should be requested from the registry.",
                revocation.Reason, revocation.RevokedAtUtc);
        }

        if (cache.Coverage is not RevocationCoverage.Complete)
        {
            return Answer(OfflineVerdict.Unknown, fields,
                "The signature is genuine, but this device cannot confirm the certificate is still "
                + $"valid. {cache.Detail} Check it at a connected office before relying on it.");
        }

        return Answer(OfflineVerdict.Valid, fields,
            "This certificate is signed by the Ministry and has not been withdrawn.");
    }

    private static OfflineVerification Answer(
        OfflineVerdict verdict,
        string[]? fields,
        string detail,
        RevocationReason? reason = null,
        DateTime? revokedAt = null)
        => new(
            verdict,
            fields?[2],
            fields?[3],
            fields is not null && DateOnly.TryParse(fields[4], out var dob) ? dob : null,
            fields?[5],
            fields is not null && DateTime.TryParse(fields[7], out var issued)
                ? issued.ToUniversalTime() : null,
            reason,
            revokedAt,
            detail);

    private static string Describe(RevocationReason reason) => reason switch
    {
        RevocationReason.Amended => "the record was corrected after it was issued",
        RevocationReason.SupersededAsDuplicate => "the registration was found to duplicate another",
        _ => reason.ToString()
    };
}
