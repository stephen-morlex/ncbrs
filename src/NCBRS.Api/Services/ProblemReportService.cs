using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

public enum ProblemReportResult
{
    Stored,
    AlreadyStored,
    NotPermitted,
    NotFound,
    AlreadyResolved,
}

public sealed record ProblemReportOutcome(ProblemReportResult Result, ProblemReportResponse? Report = null, string? Detail = null);

/// <summary>
/// Problems reported from tablets (plan §17 item 37), for the district that
/// supports them.
///
/// <para><b>Idempotent by the tablet's id.</b> A post holds a report until it
/// has signal, and a link that drops after the registry stored it means the
/// tablet sends it again. The second copy answers with the first.</para>
///
/// <para><b>Resolving is the district's.</b> Unlike a silence alert, which only
/// the device reporting again can close, a problem report says what a person
/// saw, and only a person can say it was dealt with.</para>
/// </summary>
public class ProblemReportService(NcbrsDbContext db, CountyLookup counties, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ProblemReportOutcome> StoreAsync(
        ReportProblemRequest request, Registrar reporter, Guid? transactionId, CancellationToken cancellationToken = default)
    {
        var existing = await db.DeviceProblemReports.AsNoTracking()
            .FirstOrDefaultAsync(report => report.DeviceProblemReportId == request.ReportId, cancellationToken);

        if (existing is not null)
        {
            // The same id from another device is not a resend: refuse it
            // rather than answer with a report this device never wrote.
            return existing.DeviceId == request.DeviceId
                ? new(ProblemReportResult.AlreadyStored, await ResponseAsync(existing, cancellationToken))
                : new(ProblemReportResult.NotPermitted, Detail: "That report id belongs to another device.");
        }

        var county = await counties.ForFacilityAsync(request.FacilityId, cancellationToken);
        var report = new DeviceProblemReport
        {
            DeviceProblemReportId = request.ReportId,
            DeviceId = request.DeviceId,
            FacilityId = request.FacilityId,
            CountyCode = county,
            ReportedByRegistrarId = reporter.RegistrarId,
            WrittenAtUtc = UtcTime.AsUtc(request.WrittenAtUtc),
            ReceivedAtUtc = clock.GetUtcNow().UtcDateTime,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            DiagnosticsJson = JsonSerializer.Serialize(request.Diagnostics, Json),
        };

        db.DeviceProblemReports.Add(report);
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(DeviceProblemReport),
            EntityId = report.DeviceProblemReportId.ToString(),
            CountyCode = county,
            Action = "ProblemReported",
            UserId = reporter.RegistrarId,
            DeviceId = request.DeviceId,
            TransactionId = transactionId,
        });

        await db.SaveChangesAsync(cancellationToken);
        return new(ProblemReportResult.Stored, await ResponseAsync(report, cancellationToken));
    }

    /// <param name="countyCode">The caller's county, or null for the Ministry. Required, so nobody gets the country by forgetting it.</param>
    public async Task<IReadOnlyList<ProblemReportResponse>> ListAsync(
        string? countyCode, bool includeResolved, CancellationToken cancellationToken = default)
    {
        var query = db.DeviceProblemReports.AsNoTracking();

        if (countyCode is not null)
        {
            query = query.Where(report => report.CountyCode == countyCode);
        }

        if (!includeResolved)
        {
            query = query.Where(report => report.ResolvedAtUtc == null);
        }

        // Newest first: a report is about now, and the newest is the one the
        // registrar is waiting on an answer to.
        var reports = await query
            .OrderByDescending(report => report.ReceivedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);

        var responses = new List<ProblemReportResponse>(reports.Count);
        foreach (var report in reports)
        {
            responses.Add(await ResponseAsync(report, cancellationToken));
        }

        return responses;
    }

    public async Task<ProblemReportOutcome> ResolveAsync(
        Guid reportId, string? note, Registrar resolver, Func<Guid, Task<bool>> mayActFor, Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var report = await db.DeviceProblemReports
            .FirstOrDefaultAsync(entry => entry.DeviceProblemReportId == reportId, cancellationToken);

        if (report is null)
        {
            return new(ProblemReportResult.NotFound, Detail: $"No problem report exists with id '{reportId}'.");
        }

        if (!await mayActFor(report.FacilityId))
        {
            return new(ProblemReportResult.NotPermitted, Detail: "This report is from a facility outside your county.");
        }

        if (report.ResolvedAtUtc is not null)
        {
            return new(ProblemReportResult.AlreadyResolved, await ResponseAsync(report, cancellationToken),
                $"This report was resolved on {UtcTime.AsUtc(report.ResolvedAtUtc.Value):yyyy-MM-dd}.");
        }

        report.ResolvedAtUtc = clock.GetUtcNow().UtcDateTime;
        report.ResolvedByRegistrarId = resolver.RegistrarId;
        report.ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(DeviceProblemReport),
            EntityId = report.DeviceProblemReportId.ToString(),
            CountyCode = report.CountyCode,
            Action = "ProblemReportResolved",
            UserId = resolver.RegistrarId,
            DeviceId = report.DeviceId,
            TransactionId = transactionId,
        });

        await db.SaveChangesAsync(cancellationToken);
        return new(ProblemReportResult.Stored, await ResponseAsync(report, cancellationToken));
    }

    private async Task<ProblemReportResponse> ResponseAsync(DeviceProblemReport report, CancellationToken cancellationToken)
    {
        var names = await db.Registrars.AsNoTracking()
            .Where(registrar => registrar.RegistrarId == report.ReportedByRegistrarId || registrar.RegistrarId == report.ResolvedByRegistrarId)
            .ToDictionaryAsync(registrar => registrar.RegistrarId, registrar => registrar.DisplayName, cancellationToken);

        var facility = await db.Facilities.AsNoTracking()
            .Where(entry => entry.FacilityId == report.FacilityId)
            .Select(entry => entry.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return new ProblemReportResponse(
            report.DeviceProblemReportId,
            report.DeviceId,
            report.FacilityId,
            facility ?? string.Empty,
            report.CountyCode,
            report.ReportedByRegistrarId,
            names.GetValueOrDefault(report.ReportedByRegistrarId, "Unknown"),
            UtcTime.AsUtc(report.WrittenAtUtc),
            UtcTime.AsUtc(report.ReceivedAtUtc),
            report.Description,
            JsonSerializer.Deserialize<DeviceDiagnostics>(report.DiagnosticsJson, Json) ?? new DeviceDiagnostics(),
            report.ResolvedAtUtc is { } resolved ? UtcTime.AsUtc(resolved) : null,
            report.ResolvedByRegistrarId is { } by ? names.GetValueOrDefault(by) : null,
            report.ResolutionNote);
    }
}
