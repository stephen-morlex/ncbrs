using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

public enum CertificateResult
{
    Issued,

    /// <summary>Already issued. Reprinting is a separate, audited act.</summary>
    AlreadyIssued,

    BirthRecordNotFound,

    /// <summary>
    /// A fetal death never receives a birth certificate (CLAUDE.md design
    /// decision #3 / draft Section 6.5.1).
    /// </summary>
    NotACertifiableEvent,

    NotPermitted,

    /// <summary>
    /// The birth was registered outside the statutory window and a district
    /// registrar has not yet verified the evidence (draft 4.1/5.3).
    /// </summary>
    LateRegistrationNotVerified,

    /// <summary>The registration was voided; there is no birth to certify.</summary>
    RecordAnnulled,

    /// <summary>
    /// The record still carries the provisional identifier its device issued
    /// after exhausting its BRN block. A certificate cannot be signed over an
    /// identifier that is about to be replaced (draft 6.3).
    /// </summary>
    AwaitingBrnReconciliation
}

public record CertificateOutcome(
    CertificateResult Result,
    CertificateResponse? Response = null,
    string? Detail = null)
{
    public bool Succeeded => Result is CertificateResult.Issued;
}

/// <summary>
/// Issues and reprints birth certificates.
///
/// The signed payload is built from a fixed, pipe-delimited canonical form
/// rather than from serialized JSON. A third party verifying a printed
/// certificate must reproduce the signed bytes exactly, and JSON gives no
/// guarantee of key order or whitespace across languages -- a verifier
/// written in another stack would produce different bytes and reject a
/// genuine certificate.
/// </summary>
public class CertificateService(
    NcbrsDbContext db,
    CertificateSigner signer,
    CurrentRegistrarService currentRegistrar,
    DistrictLookup districts)
{
    public const string CanonicalVersion = "v1";

    public async Task<CertificateOutcome> IssueAsync(
        string brn,
        Registrar registrar,
        string deviceId,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadAsync(brn, cancellationToken);

        if (record is null || record.ChildPerson is null)
        {
            return NotFound(brn);
        }

        if (!currentRegistrar.CanActForFacility(registrar, record.FacilityId))
        {
            return new CertificateOutcome(CertificateResult.NotPermitted,
                Detail: "You are not permitted to issue certificates for this facility.");
        }

        // A voided registration certifies nothing. Checked before the
        // vital-event rule so an annulled record reports why it is refused
        // rather than something incidental about its type.
        if (record.AnnulledAtUtc is not null)
        {
            return new CertificateOutcome(CertificateResult.RecordAnnulled,
                Detail: $"BRN '{brn}' was annulled on {record.AnnulledAtUtc:yyyy-MM-dd}. "
                        + "No certificate can be issued against a voided registration.");
        }

        // Signing over a provisional identifier would put a value on a
        // certificate that is replaced the moment the record reconciles,
        // leaving a signed document whose subject the register no longer
        // knows by that name. The family holds the device's provisional slip
        // until then.
        if (record.ProvisionalIdentifier is not null && record.ReconciledAtUtc is null)
        {
            return new CertificateOutcome(CertificateResult.AwaitingBrnReconciliation,
                Detail: $"'{brn}' is a provisional identifier issued after the device exhausted its "
                        + "BRN block. The certificate is issued once a permanent BRN has been assigned.");
        }

        // The rule that gives this system its legal shape: a stillbirth is
        // registered as a vital event but is never certified as a birth.
        if (record.VitalEventType != VitalEventType.LiveBirth)
        {
            return new CertificateOutcome(CertificateResult.NotACertifiableEvent,
                Detail: $"BRN '{brn}' is registered as a {record.VitalEventType}. "
                        + "A birth certificate is issued only for a live birth.");
        }

        // Where the late-registration process actually bites. The record
        // exists and keeps its BRN -- a child registered late is still a
        // child who exists -- but the legal document waits until a district
        // registrar has checked the evidence behind the claimed date of
        // birth. Without this the verification step would be advisory, and
        // backdating would cost a forger nothing.
        var unverified = await db.LateRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(late => late.BirthRecordId == record.BirthRecordId
                                         && late.Status != LateRegistrationStatus.Approved,
                cancellationToken);

        if (unverified is not null)
        {
            return new CertificateOutcome(CertificateResult.LateRegistrationNotVerified,
                Detail: unverified.Status == LateRegistrationStatus.Rejected
                    ? $"The late registration for BRN '{brn}' was refused on "
                      + $"{unverified.ReviewedAtUtc:yyyy-MM-dd}. No certificate can be issued against it."
                    : $"BRN '{brn}' was registered {unverified.DaysLate} days after the birth and is "
                      + "awaiting verification by a district registrar. The certificate cannot be "
                      + "issued until that is complete.");
        }

        var existing = await db.Certificates
            .FirstOrDefaultAsync(c => c.BirthRecordId == record.BirthRecordId && c.WithdrawnAtUtc == null,
                cancellationToken);

        if (existing is not null)
        {
            return new CertificateOutcome(CertificateResult.AlreadyIssued,
                ToResponse(record, existing),
                $"A certificate has already been issued for BRN '{brn}'. Use the reprint endpoint.");
        }

        // An amendment withdrew the previous certificate, so this is a
        // replacement rather than a first issue. It is signed afresh over the
        // corrected details and dated today, because that is when this
        // document -- not the withdrawn one -- was certified.
        var isReplacement = await db.Certificates
            .AnyAsync(c => c.BirthRecordId == record.BirthRecordId, cancellationToken);

        var issuedAt = DateTime.UtcNow;
        var (qrPayload, signature) = signer.Sign(Canonical(record, issuedAt));

        var certificate = new Certificate
        {
            BirthRecordId = record.BirthRecordId,
            IssueDateUtc = issuedAt,
            SignatureHash = signature,
            QrPayload = qrPayload,
            ReprintCount = 0
        };

        db.Certificates.Add(certificate);
        await AuditAsync(brn, isReplacement ? "ReissueCertificate" : "IssueCertificate", registrar,
            deviceId, transactionId, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new CertificateOutcome(CertificateResult.Issued, ToResponse(record, certificate));
    }

    /// <summary>
    /// Re-issues the same document. The signature and issue date are
    /// deliberately unchanged: a reprint is the same legal certificate, not
    /// a new one, so re-signing would produce two documents disagreeing
    /// about when the birth was certified. Only the count moves, and it is
    /// audited -- a certificate reprinted many times is a fraud signal worth
    /// being able to see.
    /// </summary>
    public async Task<CertificateOutcome> ReprintAsync(
        string brn,
        Registrar registrar,
        string deviceId,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadAsync(brn, cancellationToken);

        if (record is null || record.ChildPerson is null)
        {
            return NotFound(brn);
        }

        if (!currentRegistrar.CanActForFacility(registrar, record.FacilityId))
        {
            return new CertificateOutcome(CertificateResult.NotPermitted,
                Detail: "You are not permitted to reprint certificates for this facility.");
        }

        // Otherwise the withdrawn-certificate branch below would tell a clerk
        // to issue a replacement, which annulment makes impossible.
        if (record.AnnulledAtUtc is not null)
        {
            return new CertificateOutcome(CertificateResult.RecordAnnulled,
                Detail: $"BRN '{brn}' was annulled on {record.AnnulledAtUtc:yyyy-MM-dd}. "
                        + "Its certificate is void and cannot be reprinted.");
        }

        var certificate = await db.Certificates
            .FirstOrDefaultAsync(c => c.BirthRecordId == record.BirthRecordId && c.WithdrawnAtUtc == null,
                cancellationToken);

        // A withdrawn certificate must not be reprintable: reprinting is the
        // one path that deliberately keeps the original signature, which is
        // exactly what an amendment made wrong.
        if (certificate is null)
        {
            var withdrawn = await db.Certificates
                .AnyAsync(c => c.BirthRecordId == record.BirthRecordId, cancellationToken);

            return new CertificateOutcome(CertificateResult.BirthRecordNotFound,
                Detail: withdrawn
                    ? $"The certificate for BRN '{brn}' was withdrawn after an amendment. Issue a replacement instead."
                    : $"No certificate has been issued for BRN '{brn}' yet.");
        }

        certificate.ReprintCount++;
        await AuditAsync(brn, "ReprintCertificate", registrar, deviceId, transactionId, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new CertificateOutcome(CertificateResult.Issued, ToResponse(record, certificate));
    }

    public async Task<CertificateResponse?> GetAsync(string brn, CancellationToken cancellationToken = default)
    {
        var record = await LoadAsync(brn, cancellationToken);

        if (record?.ChildPerson is null)
        {
            return null;
        }

        var certificate = await db.Certificates
            .FirstOrDefaultAsync(c => c.BirthRecordId == record.BirthRecordId && c.WithdrawnAtUtc == null,
                cancellationToken);

        return certificate is null ? null : ToResponse(record, certificate);
    }

    /// <summary>
    /// The exact bytes that get signed. Field order and formatting are part
    /// of the contract: change either and every previously issued
    /// certificate stops verifying, which is why the version tag leads.
    /// </summary>
    public static string Canonical(BirthRecord record, DateTime issuedAtUtc)
        => string.Join('|',
            "NCBRS",
            CanonicalVersion,
            record.Brn,
            record.ChildPerson!.FullName,
            record.DateOfBirth.ToUniversalTime().ToString("yyyy-MM-dd"),
            record.Sex.ToString(),
            record.FacilityId.ToString(),
            issuedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"));

    private Task<BirthRecord?> LoadAsync(string brn, CancellationToken cancellationToken)
        => db.BirthRecords
            .Include(r => r.ChildPerson)
            .Include(r => r.Facility)
            .FirstOrDefaultAsync(r => r.Brn == brn, cancellationToken);

    // The record's district, not the issuing registrar's. A certificate is
    // issued against a record, and the district whose register it certifies
    // is the one that must be able to read the trail of it.
    private async Task AuditAsync(
        string brn, string action, Registrar registrar, string deviceId, Guid? transactionId,
        CancellationToken cancellationToken)
        => db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Certificate),
            EntityId = brn,
            DistrictId = await districts.ForBrnAsync(brn, cancellationToken),
            Action = action,
            UserId = registrar.RegistrarId,
            DeviceId = deviceId,
            TransactionId = transactionId
        });

    private static CertificateOutcome NotFound(string brn)
        => new(CertificateResult.BirthRecordNotFound,
            Detail: $"No birth record exists with BRN '{brn}'.");

    private static CertificateResponse ToResponse(BirthRecord record, Certificate certificate)
        => new(
            certificate.CertificateId,
            record.Brn,
            record.ChildPerson!.FullName,
            record.DateOfBirth,
            record.Sex,
            record.Facility?.Name ?? string.Empty,
            certificate.IssueDateUtc,
            certificate.QrPayload,
            certificate.SignatureHash,
            certificate.ReprintCount);
}
