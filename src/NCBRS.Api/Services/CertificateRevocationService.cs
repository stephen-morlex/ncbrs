
using Microsoft.EntityFrameworkCore;
using NCBRS.Certificates;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Publishes which certificates have stopped being valid.
///
/// This exists because of a limit in the signing design that nothing else
/// can fix: a printed certificate verifies against its own contents forever,
/// since the signature over them is genuine. Correcting the register cannot
/// reach into a document already in a family's hands. The only remedy is a
/// list the verifier checks as well as the signature -- so signature alone
/// stops being sufficient, and "valid" becomes "signed AND not revoked".
/// </summary>
public class CertificateRevocationService(
    NcbrsDbContext db,
    CertificateSigner signer,
    IOptions<CertificateRevocationOptions> options)
{
    public const string ListVersion = "v1";
    public const string ListIssuer = "NCBRS";

    private readonly CertificateRevocationOptions _options = options.Value;

    /// <summary>
    /// Builds and signs the list.
    ///
    /// Passing <paramref name="since"/> returns only what changed after that
    /// moment, so a device that already holds an older copy does not
    /// re-download the whole national list over a village link. The window
    /// is inside the signature, so a delta cannot be presented as complete.
    /// </summary>
    public async Task<CertificateRevocationList> BuildAsync(
        DateTime? since = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.CertificateRevocations.AsNoTracking();

        if (since is { } from)
        {
            query = query.Where(revocation => revocation.RevokedAtUtc > from);
        }

        var entries = await query
            .Select(revocation => new RevocationEntry(
                revocation.SerialHash, revocation.Reason, revocation.RevokedAtUtc))
            .ToListAsync(cancellationToken);

        // Ordering and UTC stamping both come from the shared canonical
        // helper, so the bytes signed here are produced by exactly the code
        // an offline verifier re-derives them with.
        var ordered = RevocationListCanonical.Order(entries);

        var issuedAt = DateTime.UtcNow;
        var nextUpdate = issuedAt.Add(_options.ValidFor);
        var (_, signature) = signer.Sign(
            RevocationListCanonical.Build(since, issuedAt, nextUpdate, ordered));

        return new CertificateRevocationList(
            ListIssuer,
            ListVersion,
            signer.KeyId,
            since,
            issuedAt,
            nextUpdate,
            ordered.Count,
            ordered,
            signature);
    }

    /// <summary>
    /// Checks a scanned certificate: the signature first, then the list.
    ///
    /// This is the online path, for a verifier that has a connection. The
    /// offline path is the same two checks done on the device, against a
    /// cached copy of <see cref="BuildAsync"/>.
    /// </summary>
    public async Task<VerifyCertificateResponse> VerifyAsync(
        string qrPayload,
        CancellationToken cancellationToken = default)
    {
        var canonical = signer.Verify(qrPayload);

        if (canonical is null)
        {
            // No detail about *why*: a tampered payload and a malformed one
            // are both simply not valid, and saying which would help someone
            // iterate towards a forgery.
            return new VerifyCertificateResponse(
                Valid: false,
                Reason: "The certificate could not be verified against the Ministry signing key.");
        }

        // Shape: NCBRS|v1|brn|childName|dob|sex|facilityId|issuedAt
        var fields = canonical.Split('|');

        if (fields.Length != 8)
        {
            return new VerifyCertificateResponse(
                Valid: false,
                Reason: "The certificate payload is not in a recognised format.");
        }

        // The signature travels as the QR's last segment, and it is what the
        // published list names a revoked document by.
        // Computed before the query: EF cannot translate a hash into SQL, and
        // leaving it in the predicate would silently pull every revocation
        // in the country into memory to filter it here.
        var serial = CertificateRevocationRecorder.SerialFor(qrPayload.Split('.')[^1]);

        var revocation = await db.CertificateRevocations
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.SerialHash == serial, cancellationToken);

        if (revocation is not null)
        {
            // Still returns the document's own details: the holder is not
            // the forger here, and a clerk needs to see whose certificate
            // to tell them to replace.
            return new VerifyCertificateResponse(
                Valid: false,
                Brn: fields[2],
                ChildFullName: fields[3],
                DateOfBirth: DateOnly.TryParse(fields[4], out var revokedDob) ? revokedDob : null,
                Sex: fields[5],
                IssueDateUtc: DateTime.TryParse(fields[7], out var revokedIssued)
                    ? revokedIssued.ToUniversalTime() : null,
                Reason: $"This certificate was withdrawn on {revocation.RevokedAtUtc:yyyy-MM-dd} "
                        + $"({Describe(revocation.Reason)}). A replacement should be requested from the registry.",
                Revoked: true,
                RevocationReason: revocation.Reason,
                RevokedAtUtc: revocation.RevokedAtUtc);
        }

        return new VerifyCertificateResponse(
            Valid: true,
            Brn: fields[2],
            ChildFullName: fields[3],
            DateOfBirth: DateOnly.TryParse(fields[4], out var dob) ? dob : null,
            Sex: fields[5],
            IssueDateUtc: DateTime.TryParse(fields[7], out var issued) ? issued.ToUniversalTime() : null);
    }

    private static string Describe(RevocationReason reason) => reason switch
    {
        RevocationReason.Amended => "the record was corrected after it was issued",
        RevocationReason.SupersededAsDuplicate => "the registration was found to duplicate another",
        _ => reason.ToString()
    };
}
