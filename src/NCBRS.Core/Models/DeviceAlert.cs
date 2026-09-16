namespace NCBRS.Models;

public enum DeviceAlertKind
{
    /// <summary>
    /// Enrolled, and has never once reported.
    ///
    /// Its own kind because the remedy is different. A device that used to
    /// sync and stopped is a link or a battery; a device that has never
    /// synced at all is a deployment that failed on the day it was handed
    /// over — nobody finished the setup, or the tablet is still in its box.
    /// Folding the two together sends a district officer to diagnose a
    /// network problem that was never the problem.
    /// </summary>
    NeverReported,

    /// <summary>Reported before, and has not for longer than its facility's threshold.</summary>
    Silent
}

public enum DeviceAlertStatus
{
    Open,

    /// <summary>
    /// A district officer has seen it and is acting.
    ///
    /// Deliberately not the same as resolved: acknowledging says "I know, I
    /// am driving out there on Thursday". Only the device reporting again
    /// says the problem is over. Letting an acknowledgement close the alert
    /// would let a district clear its queue without a single device coming
    /// back.
    /// </summary>
    Acknowledged,

    /// <summary>The device reported again. Set automatically, never by hand.</summary>
    Resolved
}

/// <summary>
/// A device that has gone quiet (plan F4).
///
/// "A silent device is indistinguishable from a district with no births, and
/// only one of those needs intervention" — the plan's own note, and the whole
/// reason this exists. A village post whose tablet died stops producing
/// registrations, and every dashboard in the country reports that as a period
/// with fewer births. The births still happened; nobody has them.
///
/// Raised against the registry rather than the reporting projection, because
/// the registry is the only place that knows a device exists before it has
/// ever sent anything. The projection can only see devices it has heard from,
/// which by definition excludes the worst case.
/// </summary>
public class DeviceAlert
{
    public Guid DeviceAlertId { get; set; } = Guid.CreateVersion7();

    public required string DeviceId { get; set; }

    public Guid FacilityId { get; set; }

    public Facility? Facility { get; set; }

    /// <summary>
    /// Copied rather than joined, because this is who the alert is *for*:
    /// a district officer's queue should not change shape retrospectively if
    /// a facility is later moved between districts.
    /// </summary>
    public required string DistrictId { get; set; }

    public DeviceAlertKind Kind { get; set; }

    public DeviceAlertStatus Status { get; set; } = DeviceAlertStatus.Open;

    public DateTime RaisedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null for <see cref="DeviceAlertKind.NeverReported"/>.</summary>
    public DateTime? LastSeenAtUtc { get; set; }

    /// <summary>
    /// Days of silence when the alert was raised. Kept as written rather than
    /// recomputed on read: an alert resolved after eleven days should still
    /// say eleven a year later, not however long ago it happens to be now.
    /// </summary>
    public int DaysSilentWhenRaised { get; set; }

    /// <summary>The threshold this facility's connectivity profile allowed.</summary>
    public int ThresholdDays { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }

    public Guid? AcknowledgedByRegistrarId { get; set; }

    public string? AcknowledgementNote { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }
}

public record AcknowledgeDeviceAlertRequest
{
    public string? Note { get; init; }
}

public record DeviceAlertResponse(
    Guid DeviceAlertId,
    string DeviceId,
    Guid FacilityId,
    string DistrictId,
    DeviceAlertKind Kind,
    DeviceAlertStatus Status,
    DateTime RaisedAtUtc,
    DateTime? LastSeenAtUtc,
    int DaysSilentWhenRaised,
    int ThresholdDays,
    DateTime? AcknowledgedAtUtc,
    string? AcknowledgementNote,
    DateTime? ResolvedAtUtc);
