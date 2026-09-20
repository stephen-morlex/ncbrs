using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Events;
using NCBRS.Kafka;
using NCBRS.Models;

namespace NCBRS.Services;

public enum OutcomeResult
{
    Recorded,

    BirthRecordNotFound,

    /// <summary>The outcome is already on file. One birth event, one outcome of each kind.</summary>
    AlreadyRecorded,

    /// <summary>The caller may not act for the facility that holds this record.</summary>
    NotPermitted,

    /// <summary>The domain rule was broken -- see Detail.</summary>
    Rejected,

    /// <summary>The registration was voided; there is no birth event to attach to.</summary>
    RecordAnnulled
}

public record OutcomeOutcome<T>(OutcomeResult Result, T? Response = default, string? Detail = null)
{
    public bool Succeeded => Result is OutcomeResult.Recorded;

    /// <summary>Re-shapes a failure for a different payload type.</summary>
    public OutcomeOutcome<TOther> As<TOther>() => new(Result, default, Detail);
}

/// <summary>
/// Records what happened after a birth event.
///
/// The rules here are WHO/UN definitions rather than local preference, which
/// is why they are enforced server-side instead of trusted from the device:
/// a neonatal death is one within 28 days of a LIVE birth, and a maternal
/// death is one within 42 days of the end of pregnancy. Numbers outside
/// those windows are a different vital event and must not be filed as these.
/// </summary>
public class OutcomeService(
    NcbrsDbContext db,
    IEventPublisher eventPublisher,
    CurrentRegistrarService currentRegistrar,
    CountyLookup districts)
{
    /// <summary>WHO: a neonatal death occurs within 28 completed days of a live birth.</summary>
    public const int NeonatalWindowDays = 28;

    /// <summary>WHO: a maternal death occurs within 42 days of the termination of pregnancy.</summary>
    public const int MaternalWindowDays = 42;

    public async Task<OutcomeOutcome<NeonatalOutcomeResponse>> RecordNeonatalAsync(
        string brn,
        RecordNeonatalOutcomeRequest request,
        Registrar registrar,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var (record, failure, detail) = await LoadForWriteAsync(brn, registrar, cancellationToken);
        if (failure is not null)
        {
            return new OutcomeOutcome<NeonatalOutcomeResponse>(failure.Value, default, detail);
        }

        // A neonatal outcome presupposes the child was born alive. A fetal
        // death never was, so attaching one would assert a live birth that
        // never happened -- and the fetal death's own timing belongs on that
        // record, not here.
        if (record!.VitalEventType != VitalEventType.LiveBirth)
        {
            return new OutcomeOutcome<NeonatalOutcomeResponse>(OutcomeResult.Rejected, default,
                $"BRN '{brn}' is registered as a {record.VitalEventType}. "
                + "A neonatal outcome can only follow a live birth.");
        }

        if (await db.NeonatalOutcomes.AnyAsync(o => o.BirthRecordId == record.BirthRecordId, cancellationToken))
        {
            return new OutcomeOutcome<NeonatalOutcomeResponse>(OutcomeResult.AlreadyRecorded, default,
                $"A neonatal outcome is already recorded for BRN '{brn}'.");
        }

        var days = DaysAfterBirth(record, request.DeathDateUtc);

        if (days < 0)
        {
            return new OutcomeOutcome<NeonatalOutcomeResponse>(OutcomeResult.Rejected, default,
                "deathDateUtc cannot be before the date of birth.");
        }

        if (days > NeonatalWindowDays)
        {
            return new OutcomeOutcome<NeonatalOutcomeResponse>(OutcomeResult.Rejected, default,
                $"A death {days} days after birth is outside the {NeonatalWindowDays}-day neonatal window "
                + "and is not a neonatal death under WHO ICD-PM.");
        }

        // Antepartum and Intrapartum classify stillbirths. A live birth that
        // subsequently dies is Neonatal by definition, so anything else here
        // is a miscoding that would corrupt national perinatal statistics.
        if (request.IcdPmTiming != IcdPmTiming.Neonatal)
        {
            return new OutcomeOutcome<NeonatalOutcomeResponse>(OutcomeResult.Rejected, default,
                $"icdPmTiming '{request.IcdPmTiming}' classifies a stillbirth. "
                + "A death following a live birth is Neonatal under WHO ICD-PM.");
        }

        db.NeonatalOutcomes.Add(new NeonatalOutcome
        {
            BirthRecordId = record.BirthRecordId,
            DeathDateUtc = request.DeathDateUtc,
            IcdPmTiming = request.IcdPmTiming,
            IcdPmCauseCode = request.IcdPmCauseCode,
            ContributingMaternalConditionCode = request.ContributingMaternalConditionCode,
            RecordedByRegistrarId = registrar.RegistrarId
        });

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(NeonatalOutcome),
            EntityId = brn,
            DistrictId = await districts.ForBrnAsync(brn, cancellationToken),
            Action = "RecordNeonatalOutcome",
            UserId = registrar.RegistrarId,
            DeviceId = request.DeviceId,
            TransactionId = transactionId
        });

        // Staged before the save so the outcome and its event commit together.
        eventPublisher.EnqueueNeonatalOutcome(
            new NeonatalOutcomeRecordedEvent(
                brn,
                record.BirthRecordId,
                record.FacilityId,
                request.DeathDateUtc,
                request.IcdPmTiming.ToString(),
                request.IcdPmCauseCode,
                days,
                DateTime.UtcNow,
                transactionId),
            record.Facility?.DistrictId ?? string.Empty);

        await db.SaveChangesAsync(cancellationToken);

        return new OutcomeOutcome<NeonatalOutcomeResponse>(
            OutcomeResult.Recorded,
            new NeonatalOutcomeResponse(
                record.BirthRecordId,
                brn,
                request.DeathDateUtc,
                request.IcdPmTiming,
                request.IcdPmCauseCode,
                request.ContributingMaternalConditionCode,
                days));
    }

    public async Task<OutcomeOutcome<MaternalOutcomeResponse>> RecordMaternalAsync(
        string brn,
        RecordMaternalOutcomeRequest request,
        Registrar registrar,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var (record, failure, detail) = await LoadForWriteAsync(brn, registrar, cancellationToken);
        if (failure is not null)
        {
            return new OutcomeOutcome<MaternalOutcomeResponse>(failure.Value, default, detail);
        }

        if (await db.MaternalOutcomes.AnyAsync(o => o.BirthRecordId == record!.BirthRecordId, cancellationToken))
        {
            return new OutcomeOutcome<MaternalOutcomeResponse>(OutcomeResult.AlreadyRecorded, default,
                $"A maternal outcome is already recorded for BRN '{brn}'.");
        }

        var days = DaysAfterBirth(record!, request.DeathDateUtc);

        if (days < 0)
        {
            return new OutcomeOutcome<MaternalOutcomeResponse>(OutcomeResult.Rejected, default,
                "deathDateUtc cannot be before the date of birth.");
        }

        if (days > MaternalWindowDays)
        {
            return new OutcomeOutcome<MaternalOutcomeResponse>(OutcomeResult.Rejected, default,
                $"A death {days} days after the birth is outside the {MaternalWindowDays}-day window "
                + "and is a late maternal death, not a maternal death under WHO ICD-MM.");
        }

        db.MaternalOutcomes.Add(new MaternalOutcome
        {
            BirthRecordId = record!.BirthRecordId,
            DeathDateUtc = request.DeathDateUtc,
            IcdMmCauseCode = request.IcdMmCauseCode,
            RecordedByRegistrarId = registrar.RegistrarId
        });

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(MaternalOutcome),
            EntityId = brn,
            DistrictId = await districts.ForBrnAsync(brn, cancellationToken),
            Action = "RecordMaternalOutcome",
            UserId = registrar.RegistrarId,
            DeviceId = request.DeviceId,
            TransactionId = transactionId
        });

        eventPublisher.EnqueueMaternalOutcome(
            new MaternalOutcomeRecordedEvent(
                brn,
                record.BirthRecordId,
                record.FacilityId,
                request.DeathDateUtc,
                request.IcdMmCauseCode,
                days,
                DateTime.UtcNow,
                transactionId),
            record.Facility?.DistrictId ?? string.Empty);

        await db.SaveChangesAsync(cancellationToken);

        return new OutcomeOutcome<MaternalOutcomeResponse>(
            OutcomeResult.Recorded,
            new MaternalOutcomeResponse(
                record.BirthRecordId,
                brn,
                request.DeathDateUtc,
                request.IcdMmCauseCode,
                days));
    }

    private async Task<(BirthRecord? Record, OutcomeResult? Failure, string? Detail)> LoadForWriteAsync(
        string brn,
        Registrar registrar,
        CancellationToken cancellationToken)
    {
        var record = await db.BirthRecords
            .Include(r => r.Facility)
            .FirstOrDefaultAsync(r => r.Brn == brn, cancellationToken);

        if (record is null)
        {
            return (null, OutcomeResult.BirthRecordNotFound, $"No birth record exists with BRN '{brn}'.");
        }

        if (!currentRegistrar.CanActForFacility(registrar, record.FacilityId))
        {
            return (null, OutcomeResult.NotPermitted, "You are not permitted to record outcomes for this facility.");
        }

        // Guarded here rather than in each caller so both the neonatal and
        // maternal paths are covered by one rule. An outcome attaches a death
        // to a birth event; a voided registration describes no birth event,
        // so the attachment would assert something the register has withdrawn.
        if (record.AnnulledAtUtc is not null)
        {
            return (null, OutcomeResult.RecordAnnulled,
                $"BRN '{brn}' was annulled on {record.AnnulledAtUtc:yyyy-MM-dd}. "
                + "An outcome cannot be recorded against a voided registration.");
        }

        return (record, null, null);
    }

    /// <summary>
    /// Whole days between birth and death. Compared on dates rather than
    /// instants so a death late on day 28 isn't pushed outside the window by
    /// a few hours' difference in time of day.
    /// </summary>
    private static int DaysAfterBirth(BirthRecord record, DateTime deathDateUtc)
        => (deathDateUtc.ToUniversalTime().Date - record.DateOfBirth.ToUniversalTime().Date).Days;
}
