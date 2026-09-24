using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Events;
using NCBRS.Kafka;
using NCBRS.Models;

namespace NCBRS.Services;

public enum AnnulmentResult
{
    Annulled,
    BirthRecordNotFound,
    NotPermitted,

    /// <summary>Already void. Annulling twice would publish two withdrawals of one identity.</summary>
    AlreadyAnnulled,

    /// <summary>A court-ordered annulment must name the order.</summary>
    AuthorityReferenceRequired
}

public record AnnulmentOutcome(
    AnnulmentResult Result,
    AnnulRecordResponse? Response = null,
    string? Detail = null)
{
    public bool Succeeded => Result is AnnulmentResult.Annulled;
}

/// <summary>
/// Voids a registration that should never have existed.
///
/// The heaviest action in the system, and the one most carefully bounded.
/// An amendment says the register described a real birth slightly wrongly. A
/// duplicate supersession says two records describe one child, and one of
/// them survives. This says there was no such birth at all -- it withdraws a
/// legal identity, and the person it belonged to, if there is one, has none
/// from this record afterwards.
///
/// Nothing is deleted and the BRN is never returned to the pool. A number
/// that has circulated -- printed on a certificate, quoted in a school
/// register -- must keep resolving to something that explains what became of
/// it, indefinitely.
/// </summary>
public class AnnulmentService(
    NcbrsDbContext db,
    IEventPublisher eventPublisher,
    CertificateRevocationRecorder revocations,
    CurrentRegistrarService currentRegistrar,
    CountyLookup districts)
{
    public async Task<AnnulmentOutcome> AnnulAsync(
        string brn,
        AnnulRecordRequest request,
        Registrar registrar,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        // A court-ordered annulment that cannot name its order is not a
        // court-ordered annulment.
        if (request.Reason == AnnulmentReason.CourtOrdered
            && string.IsNullOrWhiteSpace(request.AuthorityReference))
        {
            return new AnnulmentOutcome(AnnulmentResult.AuthorityReferenceRequired,
                Detail: "A court-ordered annulment must cite the order authorising it.");
        }

        var record = await db.BirthRecords
            .Include(r => r.ChildPerson)
            .Include(r => r.Facility)
            .FirstOrDefaultAsync(r => r.Brn == brn, cancellationToken);

        if (record is null || record.ChildPerson is null)
        {
            return new AnnulmentOutcome(AnnulmentResult.BirthRecordNotFound,
                Detail: $"No birth record exists with BRN '{brn}'.");
        }

        if (record.AnnulledAtUtc is not null)
        {
            return new AnnulmentOutcome(AnnulmentResult.AlreadyAnnulled,
                Detail: $"BRN '{brn}' was already annulled on {record.AnnulledAtUtc:yyyy-MM-dd}.");
        }

        // Facility scoping still applies. Annulment is ministry-only by policy,
        // and a ministry admin acts nationally, so in practice this passes; it
        // is kept so that if the policy is ever widened, the new role inherits
        // the same facility/county boundary as every other write rather than
        // silently gaining national reach.
        if (!await currentRegistrar.CanActForFacilityAsync(registrar, record.FacilityId, cancellationToken))
        {
            return new AnnulmentOutcome(AnnulmentResult.NotPermitted,
                Detail: "You are not permitted to annul records for this facility.");
        }

        var annulledAt = DateTime.UtcNow;

        var certificate = await db.Certificates
            .FirstOrDefaultAsync(c => c.BirthRecordId == record.BirthRecordId && c.WithdrawnAtUtc == null,
                cancellationToken);

        var certificateRevoked = certificate is not null;

        if (certificate is not null)
        {
            // Unlike an amendment, this revokes whatever signed fields say:
            // the document certifies a birth the register no longer holds.
            revocations.Revoke(
                certificate,
                RevocationReason.RegistrationAnnulled,
                annulledAt,
                registrar.RegistrarId,
                transactionId,
                detail: $"Registration annulled ({request.Reason}): {request.Justification}");
        }

        var annulment = new RecordAnnulment
        {
            BirthRecord = record,
            Reason = request.Reason,
            Justification = request.Justification,
            AuthorityReference = request.AuthorityReference,
            AnnulledByRegistrarId = registrar.RegistrarId,
            AnnulledAtUtc = annulledAt,
            CertificateRevoked = certificateRevoked,
            TransactionId = transactionId
        };

        db.RecordAnnulments.Add(annulment);

        record.Status = RecordStatus.Annulled;
        record.AnnulledAtUtc = annulledAt;

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(BirthRecord),
            EntityId = brn,
            CountyCode = await districts.ForBrnAsync(brn, cancellationToken),
            Action = "Annul",
            UserId = registrar.RegistrarId,
            DeviceId = "annulment",
            TransactionId = transactionId
        });

        eventPublisher.EnqueueBirthRecordAnnulled(
            new BirthRecordAnnulledEvent(
                brn,
                record.BirthRecordId,
                record.FacilityId,
                request.Reason.ToString(),
                request.Justification,
                request.AuthorityReference,
                registrar.RegistrarId,
                certificateRevoked,
                annulledAt,
                transactionId),
            record.Facility?.CountyCode ?? string.Empty);

        await db.SaveChangesAsync(cancellationToken);

        return new AnnulmentOutcome(
            AnnulmentResult.Annulled,
            new AnnulRecordResponse(
                brn, RecordStatus.Annulled, request.Reason,
                request.AuthorityReference, certificateRevoked, annulledAt));
    }
}
