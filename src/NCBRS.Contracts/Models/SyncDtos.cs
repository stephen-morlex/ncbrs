namespace NCBRS.Models;

/// <summary>
/// A device's outbox upload: everything it registered while offline, pushed
/// in one call when connectivity returns.
/// </summary>
public record SyncBatchRequest
{
    public string DeviceId { get; init; } = string.Empty;

    public Guid FacilityId { get; init; }

    public IReadOnlyList<SyncBirthRecord> Records { get; init; } = [];
}

/// <summary>
/// One entry from a device's outbox: the birth, plus who was unlocked on the
/// device when it was entered.
///
/// The author is carried per record rather than per batch because a village
/// post shares one tablet between staff. Crediting a whole batch to whoever
/// happened to sync it would put the wrong name against a legal record --
/// and that trail is the point of keeping it.
///
/// The birth payload itself deliberately has no registrar field. On the
/// online path the author comes from the token and cannot be asserted at
/// all; here it is claimed by the device and therefore checked (see
/// SyncController).
/// </summary>
public record SyncBirthRecord
{
    /// <summary>
    /// Omit to credit the record to whoever uploaded the batch -- the
    /// behaviour for a device that does not track per-user unlock.
    /// </summary>
    public Guid? RegisteredByRegistrarId { get; init; }

    public RegisterBirthRequest Birth { get; init; } = new();
}

public enum SyncRecordStatus
{
    Registered,

    /// <summary>
    /// Already on file. Expected rather than exceptional -- a device that
    /// didn't see the response for its last batch will re-upload it.
    /// </summary>
    Duplicate,

    Rejected
}

/// <summary>
/// One record's fate, keyed by BRN so a device can mark exactly which
/// entries in its outbox are settled and which need attention.
/// </summary>
public record SyncRecordOutcome(
    string Brn,
    SyncRecordStatus Status,
    IReadOnlyList<ApiError>? Errors = null,

    /// <summary>
    /// The permanent BRN assigned in place of a provisional identifier. The
    /// device must replace the number it is holding with this one and stop
    /// showing the provisional slip as current.
    /// </summary>
    string? AssignedBrn = null,

    /// <summary>
    /// Whether the centre matched this BRN to a block it granted and so
    /// confirmed it as permanent (draft 5.1).
    ///
    /// False on a Registered record is not a failure the device should retry:
    /// the birth is recorded and keeps its number. It means the registry
    /// cannot vouch for where that number came from, and a district officer
    /// will look at it. The device should stop showing the record as
    /// provisional only when this is true.
    /// </summary>
    bool BrnConfirmed = false,

    string? BrnConfirmationDetail = null
);

public record SyncBatchResponse(
    Guid SyncBatchId,
    SyncBatchStatus Status,
    int Submitted,
    int Registered,
    int Duplicates,
    int Rejected,
    IReadOnlyList<SyncRecordOutcome> Records
);
