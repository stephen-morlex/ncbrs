using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Events;
using NCBRS.Kafka;
using NCBRS.Models;

namespace NCBRS.Services;

public enum RegistrationOutcome
{
    Registered,

    /// <summary>
    /// The BRN is already registered. Not an error on the sync path: a device
    /// re-uploading a batch it wasn't sure had landed should be told "already
    /// have it", not have its whole batch rejected.
    /// </summary>
    Duplicate,

    FacilityNotFound,

    /// <summary>
    /// The authenticated registrar is not entitled to file births for this
    /// facility. Distinct from "not found" so a caller can't probe which
    /// facilities exist by watching the status code.
    /// </summary>
    FacilityNotPermitted,

    /// <summary>
    /// The birth falls outside the statutory window and no supporting
    /// evidence was supplied, or evidence was supplied for a birth that is
    /// inside it (draft 4.1/5.3).
    /// </summary>
    LateRegistrationEvidenceRequired,

    /// <summary>
    /// The device claims to have captured the birth at a time the server
    /// cannot accept -- before the birth, or implausibly far in the future.
    /// </summary>
    ImplausibleCaptureTime
}

public record RegistrationResult(
    RegistrationOutcome Outcome,
    BirthRecord? Record = null,
    string? Detail = null,
    LateRegistrationSummary? LateRegistration = null,

    /// <summary>
    /// Whether the centre could match the BRN to a block it granted, and so
    /// confirm it as permanent. False leaves the record provisional and
    /// reviewable -- never refused (draft 5.1).
    /// </summary>
    BrnReconciliationResult? BrnReconciliation = null)
{
    public bool Succeeded => Outcome is RegistrationOutcome.Registered;
}

/// <summary>
/// The single place a BirthRecord is created, shared by the one-at-a-time
/// registration endpoint and the offline batch sync. Keeping one
/// implementation matters more here than usual: the two paths must agree on
/// referential checks, audit trail and event publication, or a birth
/// registered while offline would end up recorded differently from the same
/// birth registered online.
/// </summary>
public class BirthRegistrationService(
    NcbrsDbContext db,
    IEventPublisher eventPublisher,
    CurrentRegistrarService currentRegistrar,
    DuplicateDetectionService duplicates,
    IOptions<StatutoryRegistrationOptions> statutory)
{
    private readonly StatutoryRegistrationOptions _statutory = statutory.Value;

    /// <summary>
    /// <paramref name="registrar"/> is the authenticated caller, resolved
    /// from the access token by the controller -- never taken from the
    /// request payload.
    /// </summary>
    /// <param name="syncBatchId">
    /// The batch that carried this record in, when it arrived from a device
    /// rather than being filed directly. Null for an online registration.
    /// </param>
    public async Task<RegistrationResult> RegisterAsync(
        RegisterBirthRequest request,
        Registrar registrar,
        Guid? transactionId,
        CancellationToken cancellationToken = default,
        Guid? syncBatchId = null)
    {
        var facility = await db.Facilities.FindAsync([request.FacilityId], cancellationToken);
        if (facility is null)
        {
            return new RegistrationResult(
                RegistrationOutcome.FacilityNotFound,
                Detail: $"No facility exists with id '{request.FacilityId}'.");
        }

        // Checked here rather than in each controller so the online and
        // offline paths cannot drift: a registrar confined to one facility
        // must not be able to file a birth against another by either route.
        if (!currentRegistrar.CanActForFacility(registrar, facility.FacilityId))
        {
            return new RegistrationResult(
                RegistrationOutcome.FacilityNotPermitted,
                Detail: $"You are not permitted to register births for facility '{facility.FacilityId}'.");
        }

        if (await db.BirthRecords.AnyAsync(record => record.Brn == request.Brn, cancellationToken))
        {
            return new RegistrationResult(
                RegistrationOutcome.Duplicate,
                Detail: $"BRN '{request.Brn}' has already been registered.");
        }

        var receivedAt = DateTime.UtcNow;
        var capturedAt = request.RegisteredAtUtc ?? receivedAt;

        // A capture time before the birth, or well ahead of the server, is
        // either a broken clock or an attempt to dodge the late process.
        if (capturedAt < request.DateOfBirth
            || capturedAt > receivedAt.Add(_statutory.ClockSkewTolerance))
        {
            return new RegistrationResult(
                RegistrationOutcome.ImplausibleCaptureTime,
                Detail: "registeredAtUtc must fall between the date of birth and now.");
        }

        var daysLate = (int)(capturedAt.Date - request.DateOfBirth.Date).TotalDays;
        var isLate = daysLate > _statutory.WindowDays;

        if (isLate && request.LateRegistration is null)
        {
            return new RegistrationResult(
                RegistrationOutcome.LateRegistrationEvidenceRequired,
                Detail: $"This birth was registered {daysLate} days after it occurred, outside the "
                        + $"{_statutory.WindowDays}-day statutory window. Supporting evidence and a "
                        + "declarant are required, and a district registrar must verify them.");
        }

        // Rejected rather than ignored: evidence attached to an on-time birth
        // means the device and the registry disagree about the date, and
        // silently dropping it would hide that.
        if (!isLate && request.LateRegistration is not null)
        {
            return new RegistrationResult(
                RegistrationOutcome.LateRegistrationEvidenceRequired,
                Detail: $"This birth was registered {daysLate} days after it occurred, inside the "
                        + $"{_statutory.WindowDays}-day statutory window, so late-registration "
                        + "evidence does not apply to it.");
        }

        var child = new Person
        {
            FullName = request.ChildFullName,
            DateOfBirth = DateOnly.FromDateTime(request.DateOfBirth)
        };
        db.People.Add(child);

        var mother = AddPersonIfNamed(request.MotherFullName);
        var father = AddPersonIfNamed(request.FatherFullName);

        var record = new BirthRecord
        {
            Brn = request.Brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = child,
            MotherPerson = mother,
            FatherPerson = father,
            FacilityId = request.FacilityId,
            RegisteredByRegistrarId = registrar.RegistrarId,
            DateOfBirth = request.DateOfBirth,
            Sex = request.Sex,
            BirthWeightGrams = request.BirthWeightGrams,
            GestationalAgeWeeks = request.GestationalAgeWeeks,
            Plurality = request.Plurality,
            BirthOrder = request.BirthOrder,
            Status = RecordStatus.Provisional
        };

        // A device that exhausted its block offline sends a provisional
        // identifier instead (draft 6.3). There is nothing to reconcile it
        // against -- it was never drawn from a block -- so it skips BRN
        // confirmation and is flagged for assignment of a real number.
        var isProvisional = ProvisionalIdentifier.Looks(request.Brn);

        if (isProvisional)
        {
            record.ProvisionalIdentifier = request.Brn;
        }

        // The reconciliation the draft calls for at 5.1: the centre checks
        // the BRN really came from a block it granted, and only then is the
        // number permanent. A record that cannot be matched stays provisional
        // rather than being refused -- the birth happened, and the family may
        // already hold a provisional certificate carrying this number.
        var reconciliation = isProvisional
            ? new BrnReconciliationResult(
                BrnReconciliation.NotYetAllocated,
                $"'{request.Brn}' is a provisional identifier issued after the device exhausted its "
                + "BRN block. A permanent BRN is assigned when the record is reconciled.")
            : BrnReconciler.Reconcile(request.Brn, facility);

        if (reconciliation.Confirmed)
        {
            record.Status = RecordStatus.Confirmed;
            record.ConfirmedAtUtc = receivedAt;
            record.ConfirmedBySyncBatchId = syncBatchId;
        }
        else
        {
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(BirthRecord),
                EntityId = request.Brn,
                // A provisional identifier is an expected consequence of an
                // exhausted block, not a device issuing numbers it was never
                // granted. Distinguished so the second stays findable.
                Action = isProvisional
                    ? "ProvisionalIdentifierAccepted"
                    : $"BrnUnconfirmed:{reconciliation.Outcome}",
                UserId = registrar.RegistrarId,
                DeviceId = request.DeviceId,
                TransactionId = transactionId
            });
        }

        db.BirthRecords.Add(record);

        LateRegistrationSummary? lateSummary = null;

        if (isLate)
        {
            var late = new LateRegistration
            {
                BirthRecord = record,
                DaysLate = daysLate,
                WindowDaysAtFiling = _statutory.WindowDays,
                EvidenceType = request.LateRegistration!.EvidenceType,
                EvidenceReference = request.LateRegistration.EvidenceReference,
                DeclarantName = request.LateRegistration.DeclarantName,
                DeclarantRelationship = request.LateRegistration.DeclarantRelationship,
                SubmittedByRegistrarId = registrar.RegistrarId,
                SubmittedAtUtc = receivedAt,
                TransactionId = transactionId
            };

            db.LateRegistrations.Add(late);

            lateSummary = new LateRegistrationSummary(
                daysLate, _statutory.WindowDays, late.Status, late.EvidenceType);

            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(LateRegistration),
                EntityId = request.Brn,
                Action = "LateRegistrationFiled",
                UserId = registrar.RegistrarId,
                DeviceId = request.DeviceId,
                TransactionId = transactionId
            });
        }

        // Captured in the same workflow as the registration (draft 6.5.1),
        // but never able to fail it: the statistical questionnaire is a
        // legally distinct function, and a birth must not go unregistered
        // because an answer about the mother's schooling was contradictory.
        if (request.MaternalStatistics is { } questionnaire)
        {
            db.MaternalStatistics.Add(new MaternalStatistics
            {
                BirthRecord = record,
                MotherEducationLevel = questionnaire.MotherEducationLevel,
                MotherOccupation = questionnaire.MotherOccupation,
                FatherEducationLevel = questionnaire.FatherEducationLevel,
                FatherOccupation = questionnaire.FatherOccupation,
                PriorLiveBirths = questionnaire.PriorLiveBirths,
                PriorFetalDeaths = questionnaire.PriorFetalDeaths,
                PrenatalVisitCount = questionnaire.PrenatalVisitCount,
                MedicalCareBeganDate = questionnaire.MedicalCareBeganDate,
                DateOfLastLiveBirth = questionnaire.DateOfLastLiveBirth,
                RecordedByRegistrarId = registrar.RegistrarId,
                RecordedAtUtc = receivedAt,
                TransactionId = transactionId
            });
        }

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(BirthRecord),
            EntityId = request.Brn,
            Action = "Create",
            UserId = registrar.RegistrarId,
            DeviceId = request.DeviceId,
            TransactionId = transactionId
        });

        // Staged BEFORE the save so the event and the birth commit together.
        // Publishing after the commit meant a rollback could announce a
        // registration that never landed, and a crash in between lost the
        // event outright.
        eventPublisher.EnqueueBirthRegistered(
            new BirthRegisteredEvent(
                record.Brn,
                record.BirthRecordId,
                record.FacilityId,
                facility.DistrictId,
                record.DateOfBirth,
                record.Sex.ToString(),
                DateTime.UtcNow,
                transactionId,
                facility.Tier.ToString(),
                record.VitalEventType.ToString(),
                !isLate,
                record.ConfirmedAtUtc),
            facility.DistrictId);

        await db.SaveChangesAsync(cancellationToken);

        // Runs after the registration is committed, never before: a
        // heuristic must not be able to refuse a real birth. It produces a
        // review queue, and swallows its own failures.
        await duplicates.ScanAsync(record.BirthRecordId, cancellationToken);

        return new RegistrationResult(
            RegistrationOutcome.Registered, record,
            LateRegistration: lateSummary,
            BrnReconciliation: reconciliation);
    }

    private Person? AddPersonIfNamed(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        var person = new Person { FullName = fullName };
        db.People.Add(person);
        return person;
    }
}
