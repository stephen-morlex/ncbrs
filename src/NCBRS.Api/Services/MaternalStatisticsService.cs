using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

public enum MaternalStatisticsResult
{
    Recorded,
    BirthRecordNotFound,
    NotPermitted,

    /// <summary>The registration was voided; there is no birth event to describe.</summary>
    RecordAnnulled,

    /// <summary>The answers contradict each other or the record -- see Detail.</summary>
    Inconsistent
}

public record MaternalStatisticsOutcome(
    MaternalStatisticsResult Result,
    MaternalStatisticsResponse? Response = null,
    string? Detail = null,

    /// <summary>True when this replaced an existing questionnaire rather than creating one.</summary>
    bool Revised = false)
{
    public bool Succeeded => Result is MaternalStatisticsResult.Recorded;
}

/// <summary>
/// The statistical questionnaire that rides alongside the legal registration
/// (draft 6.5, 6.5.1; UN P&amp;R Rev. 3).
///
/// The separation from the legal record is behavioural, not just a table
/// boundary. Nothing here gates a certificate, blocks a registration or
/// reaches the printed document, and a birth whose questionnaire is never
/// completed is a fully registered birth. That is why capture is an upsert
/// with no approval workflow: a corrected statistic is simply a better
/// statistic, not an amendment to anyone's legal identity.
///
/// What it does enforce is internal consistency. A questionnaire that
/// contradicts itself is worse than a blank one, because it aggregates
/// silently into a national figure that nobody can tell is wrong.
/// </summary>
public class MaternalStatisticsService(
    NcbrsDbContext db,
    CurrentRegistrarService currentRegistrar,
    DistrictLookup districts)
{
    /// <summary>WHO's 2016 antenatal care model recommends a minimum of eight contacts.</summary>
    public const int WhoAntenatalMinimumContacts = 8;

    public async Task<MaternalStatisticsOutcome> CaptureAsync(
        string brn,
        MaternalStatisticsRequest request,
        Registrar registrar,
        string deviceId,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var record = await db.BirthRecords
            .Include(r => r.MaternalStatistics)
            .FirstOrDefaultAsync(r => r.Brn == brn, cancellationToken);

        if (record is null)
        {
            return new MaternalStatisticsOutcome(MaternalStatisticsResult.BirthRecordNotFound,
                Detail: $"No birth record exists with BRN '{brn}'.");
        }

        if (!currentRegistrar.CanActForFacility(registrar, record.FacilityId))
        {
            return new MaternalStatisticsOutcome(MaternalStatisticsResult.NotPermitted,
                Detail: "You are not permitted to record statistics for this facility.");
        }

        if (record.AnnulledAtUtc is not null)
        {
            return new MaternalStatisticsOutcome(MaternalStatisticsResult.RecordAnnulled,
                Detail: $"BRN '{brn}' was annulled on {record.AnnulledAtUtc:yyyy-MM-dd}. "
                        + "Statistics cannot be recorded against a voided registration.");
        }

        if (Inconsistency(request, record) is { } problem)
        {
            return new MaternalStatisticsOutcome(MaternalStatisticsResult.Inconsistent, Detail: problem);
        }

        var now = DateTime.UtcNow;
        var statistics = record.MaternalStatistics;
        var revised = statistics is not null;

        if (statistics is null)
        {
            statistics = new MaternalStatistics
            {
                BirthRecordId = record.BirthRecordId,
                RecordedByRegistrarId = registrar.RegistrarId,
                RecordedAtUtc = now
            };

            db.MaternalStatistics.Add(statistics);
        }
        else
        {
            statistics.UpdatedAtUtc = now;
        }

        statistics.MotherEducationLevel = request.MotherEducationLevel;
        statistics.MotherOccupation = request.MotherOccupation;
        statistics.FatherEducationLevel = request.FatherEducationLevel;
        statistics.FatherOccupation = request.FatherOccupation;
        statistics.PriorLiveBirths = request.PriorLiveBirths;
        statistics.PriorFetalDeaths = request.PriorFetalDeaths;
        statistics.PrenatalVisitCount = request.PrenatalVisitCount;
        statistics.MedicalCareBeganDate = request.MedicalCareBeganDate;
        statistics.DateOfLastLiveBirth = request.DateOfLastLiveBirth;
        statistics.TransactionId = transactionId;

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(MaternalStatistics),
            EntityId = brn,
            DistrictId = await districts.ForBrnAsync(brn, cancellationToken),
            Action = revised ? "ReviseMaternalStatistics" : "RecordMaternalStatistics",
            UserId = registrar.RegistrarId,
            DeviceId = deviceId,
            TransactionId = transactionId
        });

        await db.SaveChangesAsync(cancellationToken);

        return new MaternalStatisticsOutcome(
            MaternalStatisticsResult.Recorded, ToResponse(brn, record, statistics), Revised: revised);
    }

    public async Task<MaternalStatisticsResponse?> GetAsync(
        string brn,
        CancellationToken cancellationToken = default)
    {
        var record = await db.BirthRecords
            .AsNoTracking()
            .Include(r => r.MaternalStatistics)
            .FirstOrDefaultAsync(r => r.Brn == brn, cancellationToken);

        return record?.MaternalStatistics is null
            ? null
            : ToResponse(brn, record, record.MaternalStatistics);
    }

    /// <summary>
    /// Cross-field checks. Each one catches a combination that would
    /// otherwise aggregate silently into a wrong national figure.
    /// </summary>
    private static string? Inconsistency(MaternalStatisticsRequest request, BirthRecord record)
    {
        var dateOfBirth = DateOnly.FromDateTime(record.DateOfBirth);

        if (request.DateOfLastLiveBirth is { } lastBirth)
        {
            if (request.PriorLiveBirths == 0)
            {
                return "dateOfLastLiveBirth was supplied but priorLiveBirths is 0. "
                       + "A previous live birth implies at least one prior live birth.";
            }

            if (lastBirth >= dateOfBirth)
            {
                return $"dateOfLastLiveBirth ({lastBirth:yyyy-MM-dd}) must fall before this birth "
                       + $"({dateOfBirth:yyyy-MM-dd}).";
            }
        }

        // A pregnancy cannot have been under antenatal care before it began.
        // Well outside gestation, this is a data-entry error rather than an
        // unusually early booking.
        if (request.MedicalCareBeganDate is { } careBegan)
        {
            if (careBegan > dateOfBirth)
            {
                return $"medicalCareBeganDate ({careBegan:yyyy-MM-dd}) cannot fall after the birth "
                       + $"({dateOfBirth:yyyy-MM-dd}).";
            }

            if (careBegan < dateOfBirth.AddMonths(-11))
            {
                return $"medicalCareBeganDate ({careBegan:yyyy-MM-dd}) is more than eleven months before "
                       + "the birth, which is outside any plausible pregnancy.";
            }
        }

        // Antenatal care that began but recorded no contacts is a
        // contradiction: the booking visit is itself a contact.
        if (request.MedicalCareBeganDate is not null && request.PrenatalVisitCount == 0)
        {
            return "prenatalVisitCount is 0 but medicalCareBeganDate was supplied. "
                   + "The booking visit is itself an antenatal contact.";
        }

        return null;
    }

    private static MaternalStatisticsResponse ToResponse(
        string brn, BirthRecord record, MaternalStatistics statistics)
    {
        int? interval = statistics.DateOfLastLiveBirth is { } last
            ? MonthsBetween(last, DateOnly.FromDateTime(record.DateOfBirth))
            : null;

        return new MaternalStatisticsResponse(
            brn,
            statistics.MotherEducationLevel,
            statistics.MotherOccupation,
            statistics.FatherEducationLevel,
            statistics.FatherOccupation,
            statistics.PriorLiveBirths,
            statistics.PriorFetalDeaths,
            statistics.PrenatalVisitCount,
            statistics.MedicalCareBeganDate,
            statistics.DateOfLastLiveBirth,
            interval,
            statistics.PrenatalVisitCount is { } visits
                ? visits >= WhoAntenatalMinimumContacts
                : null,
            statistics.RecordedAtUtc,
            statistics.UpdatedAtUtc);
    }

    /// <summary>
    /// Whole months elapsed. Counted on calendar months rather than by
    /// dividing days, so a 24-month interval reads as 24 regardless of which
    /// months it spans -- the WHO spacing threshold sits exactly there.
    /// </summary>
    private static int MonthsBetween(DateOnly from, DateOnly to)
    {
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;

        return to.Day < from.Day ? months - 1 : months;
    }
}
