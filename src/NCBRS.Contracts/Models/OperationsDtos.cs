namespace NCBRS.Models;

/// <summary>
/// How each facility in scope is doing, for the people who keep a pilot running
/// (plan §17 item 35). Read from the registry, not the reporting projection:
/// most of this is work waiting on someone, which only the registry holds.
/// </summary>
/// <param name="WindowDays">The recent period the "in window" figures cover.</param>
public record FacilityOperationsReport(
    DateTime GeneratedAtUtc,
    int WindowDays,
    IReadOnlyList<FacilityOperations> Facilities);

/// <summary>A queue's size and how long its oldest item has waited.</summary>
public record Backlog(int Count, DateTime? OldestAtUtc);

public record FacilityOperations(
    Guid FacilityId,
    string Name,
    string CountyCode,
    FacilityTier Tier,
    ConnectivityProfile ConnectivityProfile,

    /// <summary>Registrations that reached the registry in the window.</summary>
    int RegistrationsInWindow,

    /// <summary>When a batch from this facility last reached the registry. Null: never.</summary>
    DateTime? LastSyncAtUtc,

    /// <summary>Devices enrolled and not suspended or revoked.</summary>
    int ActiveDevices,

    /// <summary>Silence alerts not yet resolved, acknowledged or not.</summary>
    int OpenDeviceAlerts,

    /// <summary>
    /// Records synced in the window, from batches that recorded what became of
    /// them. Null when no such batch arrived in the window: not recorded, which
    /// is not the same as none.
    /// </summary>
    int? SyncedRecordsInWindow,

    /// <summary>Of those, how many the registry refused. Null exactly when <see cref="SyncedRecordsInWindow"/> is.</summary>
    int? RefusedRecordsInWindow,

    /// <summary>Records still under a device's provisional identifier, waiting for a real number.</summary>
    int ProvisionalOpen,

    /// <summary>
    /// Records whose number the registry could not confirm against the blocks it
    /// granted: a device numbering outside its range. Never refused, always worth a look.
    /// </summary>
    int UnconfirmedNumbers,

    Backlog PendingCorrections,
    Backlog PendingLateRegistrations,
    Backlog PendingDuplicates,

    int CertificatesToIssue,
    int CertificatesToHandOver,

    /// <summary>When the birth that has waited longest for its certificate was registered.</summary>
    DateTime? OldestUncollectedRegisteredAtUtc);
