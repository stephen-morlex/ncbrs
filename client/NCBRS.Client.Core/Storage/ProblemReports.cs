using NCBRS.Client.Time;
using NCBRS.Models;

namespace NCBRS.Client.Storage;

/// <summary>What the app says about itself, which the core cannot know.</summary>
public sealed record AppInfo(string Version, string Platform, string Language);

/// <summary>
/// "Report a problem" (plan §17 item 37): the tablet's own account of its
/// state, built from what it holds, plus what the registrar wrote.
///
/// <para><b>Counts, versions and times only.</b> No names, no registration
/// numbers, no addresses: a report travels to the district and sits in a queue
/// several people read. The registrar's own words are the one free-text field,
/// and the screen asks them not to write names in it.</para>
///
/// <para>Built from a state the session has just been captured into, so the
/// figures are the ones the registrar is looking at.</para>
/// </summary>
public static class ProblemReports
{
    public const int MaxDescription = 1000;
    public const int MaxProblems = 5;
    public const int MaxProblemLength = 300;

    public static ReportProblemRequest Build(
        DeviceState state,
        AppInfo app,
        ClockReading clock,
        string? description,
        IReadOnlyList<string> lastSyncProblems,
        Guid? reportId = null)
    {
        var identity = state.Identity ?? throw new InvalidOperationException("A tablet that has not been handed over has no one to report to.");

        return new ReportProblemRequest
        {
            ReportId = reportId ?? Guid.CreateVersion7(),
            DeviceId = identity.DeviceId,
            FacilityId = identity.FacilityId,
            WrittenAtUtc = clock.Utc,
            Description = Trimmed(description, MaxDescription),
            Diagnostics = Diagnose(state, app, clock, lastSyncProblems),
        };
    }

    public static DeviceDiagnostics Diagnose(DeviceState state, AppInfo app, ClockReading clock, IReadOnlyList<string> lastSyncProblems)
        => new()
        {
            AppVersion = Trimmed(app.Version, 64) ?? "",
            Platform = Trimmed(app.Platform, 64) ?? "",
            Language = Trimmed(app.Language, 64) ?? "",
            SignedIn = state.OfflineToken is not null,
            StaffCount = state.Staff.Count,
            WaitingToSync = state.Outbox.Count,
            Refused = state.Refused.Count,
            UploadInFlight = state.InFlight is not null,
            Drafts = state.Drafts.Count,
            NumbersLeft = NumbersLeft(state.Brn),
            ProvisionalIssued = state.Brn?.ProvisionalSequence ?? 0,
            LastSyncedAtUtc = state.LastSyncedAtUtc,
            BundleFetchedAtUtc = state.Bundle?.FetchedAtUtc,
            ClockVerdict = clock.Verdict.ToString(),
            ClockOffsetSeconds = Math.Round((clock.WallUtc - clock.Utc).TotalSeconds),
            FailedPinAttempts = state.Attempts.FailedAttempts,
            PinLockouts = state.Attempts.Lockouts,
            LastSyncProblems = [.. lastSyncProblems
                .Where(problem => !string.IsNullOrWhiteSpace(problem))
                .Take(MaxProblems)
                .Select(problem => Trimmed(problem, MaxProblemLength)!)],
        };

    /// <summary>What is left in the current block and any block staged behind it.</summary>
    private static long? NumbersLeft(BrnState? brn)
        => brn is null
            ? null
            : Math.Max(0, brn.BlockEnd - brn.NextAvailable + 1)
              + (brn is { PendingBlockStart: { } start, PendingBlockEnd: { } end } ? end - start + 1 : 0);

    private static string? Trimmed(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
