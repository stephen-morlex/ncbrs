using System.Security.Cryptography;
using System.Text;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Withdraws a certificate and records the fact for publication.
///
/// Separate from <see cref="CertificateRevocationService"/>, which signs and
/// serves the list, because the two need different things: recording is a
/// database write, publishing needs the Ministry's signing key. Keeping them
/// apart means the code paths that revoke -- amendment and duplicate
/// adjudication -- never take a dependency on the key at all.
/// </summary>
public class CertificateRevocationRecorder(NcbrsDbContext db)
{
    /// <summary>
    /// The identifier a revoked certificate is published under: a digest of
    /// the signature printed on it.
    ///
    /// Derived rather than stored on the certificate because the printed QR
    /// carries no serial number, and adding one would change the canonical
    /// signed payload -- which would stop every certificate already in a
    /// family's hands from verifying. A verifier holding the paper can
    /// recompute this from what is on it.
    ///
    /// Being a digest is also what makes the list publishable: it names no
    /// child, no BRN and no facility, so distributing it widely -- which a
    /// revocation list must be, to be worth anything -- leaks nothing.
    ///
    /// Truncated to 128 bits to keep the national list small enough to push
    /// to offline devices; far beyond the reach of a collision search, and
    /// an untruncated list would be twice the size for no gain.
    /// </summary>
    public static string SerialFor(string signature)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(signature));

        return Convert.ToBase64String(digest, 0, 16)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Marks a certificate withdrawn and stages its revocation entry.
    ///
    /// Both happen here rather than at the call sites, because a certificate
    /// withdrawn internally but never published is exactly the failure this
    /// mechanism exists to prevent -- and something written in two places
    /// eventually gets done in only one of them.
    ///
    /// Staged on the caller's DbContext, not saved: the revocation must
    /// commit in the same transaction as the amendment or supersession that
    /// caused it.
    /// </summary>
    public void Revoke(
        Certificate certificate,
        RevocationReason reason,
        DateTime revokedAtUtc,
        Guid? revokedByRegistrarId,
        Guid? transactionId,
        string? detail = null)
    {
        if (certificate.WithdrawnAtUtc is not null)
        {
            // Already withdrawn. Revoking twice would publish one serial
            // under two timestamps and make the list contradict itself.
            return;
        }

        certificate.WithdrawnAtUtc = revokedAtUtc;
        certificate.WithdrawnReason = detail ?? reason.ToString();

        db.CertificateRevocations.Add(new CertificateRevocation
        {
            CertificateId = certificate.CertificateId,
            BirthRecordId = certificate.BirthRecordId,
            SerialHash = SerialFor(certificate.SignatureHash),
            Reason = reason,
            RevokedAtUtc = revokedAtUtc,
            RevokedByRegistrarId = revokedByRegistrarId,
            TransactionId = transactionId
        });
    }
}
