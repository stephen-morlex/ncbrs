namespace NCBRS.Models;

/// <summary>
/// A problem a registrar reported from a tablet (plan §17 item 37), with the
/// tablet's own account of its state. A post may be days from the district
/// office, and "it doesn't work" over a bad phone line is where remote support
/// usually ends; this carries what the officer would have asked.
/// </summary>
public class DeviceProblemReport
{
    /// <summary>Generated on the tablet, so sending the same report twice stores it once.</summary>
    public Guid DeviceProblemReportId { get; set; }

    public required string DeviceId { get; set; }

    public Guid FacilityId { get; set; }

    public required string CountyCode { get; set; }

    /// <summary>The account the tablet sent it under.</summary>
    public Guid ReportedByRegistrarId { get; set; }

    /// <summary>When the registrar wrote it, by the tablet's trusted clock. It may reach the registry days later.</summary>
    public DateTime WrittenAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>What the registrar wrote, if anything.</summary>
    public string? Description { get; set; }

    /// <summary><see cref="DeviceDiagnostics"/>, as JSON.</summary>
    public required string DiagnosticsJson { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    public Guid? ResolvedByRegistrarId { get; set; }

    public string? ResolutionNote { get; set; }
}

/// <summary>
/// The tablet's state, as counts, versions and times: what a district officer
/// asks first. <b>Nothing in it names a person or a birth</b>: no names, no
/// registration numbers, no addresses. A report travels to the district and
/// sits in a queue several people read, so it carries no more than support
/// needs.
/// </summary>
public record DeviceDiagnostics
{
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>The operating system and its version, for example "Android 14".</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>The language the tablet is showing, for example "ar".</summary>
    public string Language { get; init; } = string.Empty;

    public bool SignedIn { get; init; }

    /// <summary>Staff who can unlock this tablet.</summary>
    public int StaffCount { get; init; }

    public int WaitingToSync { get; init; }

    public int Refused { get; init; }

    /// <summary>An upload sent and not yet settled.</summary>
    public bool UploadInFlight { get; init; }

    public int Drafts { get; init; }

    /// <summary>Registration numbers left in the tablet's blocks. Null before its first block.</summary>
    public long? NumbersLeft { get; init; }

    /// <summary>Provisional identifiers issued since the tablet ran out of numbers.</summary>
    public long ProvisionalIssued { get; init; }

    public DateTime? LastSyncedAtUtc { get; init; }

    /// <summary>When the certificate-checking bundle was fetched. Null: never.</summary>
    public DateTime? BundleFetchedAtUtc { get; init; }

    /// <summary>The trusted clock's verdict on the tablet's own clock: Trusted, Behind or Ahead.</summary>
    public string ClockVerdict { get; init; } = string.Empty;

    /// <summary>How far the tablet's clock is from the trusted time, in seconds (positive: ahead).</summary>
    public double ClockOffsetSeconds { get; init; }

    /// <summary>Wrong PINs counted towards the next lockout, and lockouts since the right PIN.</summary>
    public int FailedPinAttempts { get; init; }

    public int PinLockouts { get; init; }

    /// <summary>
    /// The problems the last sync reported, as the tablet showed them. System
    /// messages, capped in number and length.
    /// </summary>
    public IReadOnlyList<string> LastSyncProblems { get; init; } = [];
}

/// <summary>A report as the tablet sends it.</summary>
public record ReportProblemRequest
{
    public Guid ReportId { get; init; }

    public string DeviceId { get; init; } = string.Empty;

    public Guid FacilityId { get; init; }

    public DateTime WrittenAtUtc { get; init; }

    public string? Description { get; init; }

    public DeviceDiagnostics Diagnostics { get; init; } = new();
}

public record ResolveProblemReportRequest
{
    public string? Note { get; init; }
}

public record ProblemReportResponse(
    Guid ReportId,
    string DeviceId,
    Guid FacilityId,
    string FacilityName,
    string CountyCode,
    Guid ReportedByRegistrarId,
    string ReportedByName,
    DateTime WrittenAtUtc,
    DateTime ReceivedAtUtc,
    string? Description,
    DeviceDiagnostics Diagnostics,
    DateTime? ResolvedAtUtc,
    string? ResolvedByName,
    string? ResolutionNote);
