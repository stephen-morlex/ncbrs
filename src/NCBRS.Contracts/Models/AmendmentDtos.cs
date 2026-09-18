namespace NCBRS.Models;

/// <summary>
/// A correction to a registered birth.
///
/// Every field is optional: only what is supplied is changed, and supplying
/// a value identical to the current one records nothing. The BRN, facility
/// and vital event type are absent on purpose -- the first is permanent, and
/// changing the last would make this a different vital event rather than a
/// correction to this one.
/// </summary>
public record AmendBirthRecordRequest
{
    public string? ChildFullName { get; init; }
    public DateTime? DateOfBirth { get; init; }
    public Sex? Sex { get; init; }
    public int? BirthWeightGrams { get; init; }
    public decimal? GestationalAgeWeeks { get; init; }
    public int? BirthOrder { get; init; }
    public string? MotherFullName { get; init; }
    public string? FatherFullName { get; init; }

    /// <summary>Why the correction is being made. Required.</summary>
    public string Reason { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    /// <summary>
    /// What the device believed the record said when it composed this
    /// correction, for the fields it is changing.
    ///
    /// Sent by a device that may have been offline for weeks; omitted by an
    /// online caller, which read the record moments ago and cannot be stale.
    /// Where it is supplied and disagrees with what the register now holds,
    /// the correction is still applied — last-writer-wins, per draft 6.3 —
    /// but the clash is recorded and flagged for a registrar rather than
    /// merged silently.
    /// </summary>
    public IReadOnlyList<ObservedValue>? ObservedValues { get; init; }
}

/// <summary>
/// One field as a device last saw it. The value uses the same string form the
/// amendment history stores, so a device echoes back exactly what it was
/// given.
/// </summary>
public record ObservedValue(string Field, string? Value);

public record AmendedFieldResponse(
    string Field,
    string? PreviousValue,
    string? NewValue
);

public record AmendBirthRecordResponse(
    string Brn,
    RecordStatus Status,

    /// <summary>Identifies this submission, and what a reviewer approves.</summary>
    Guid AmendmentRequestId,

    /// <summary>Changes already in effect on the record.</summary>
    IReadOnlyList<AmendedFieldResponse> Applied,

    /// <summary>
    /// Changes waiting on a reviewer. The record still reads as it did; these
    /// take effect only once approved, and a caller must not report them to a
    /// family as done.
    /// </summary>
    IReadOnlyList<AmendedFieldResponse> PendingApproval,

    /// <summary>
    /// True when a certificate had been issued and an <em>applied</em> change
    /// touched a field its signature covers. A pending change does not
    /// withdraw anything: the certificate stays valid until the correction
    /// it contradicts is actually approved.
    /// </summary>
    bool CertificateInvalidated,

    DateTime AmendedAtUtc
);

/// <summary>A reviewer's decision on a submitted correction.</summary>
public record ReviewAmendmentRequest
{
    public bool Approve { get; init; }

    /// <summary>Why. Required when refusing.</summary>
    public string? Note { get; init; }
}

public record ReviewAmendmentResponse(
    Guid AmendmentRequestId,
    string Brn,
    AmendmentStatus Status,
    IReadOnlyList<AmendedFieldResponse> Changes,
    bool CertificateInvalidated,
    DateTime ReviewedAtUtc
);

/// <summary>One correction awaiting approval, as the reviewer queue lists it.</summary>
public record PendingAmendmentResponse(
    Guid AmendmentRequestId,
    string Brn,
    string ChildFullName,
    Guid FacilityId,
    string FacilityName,
    string Reason,
    Guid SubmittedByRegistrarId,
    string SubmittedByRegistrarName,
    DateTime SubmittedAtUtc,
    IReadOnlyList<AmendedFieldResponse> Changes
);

/// <summary>
/// One row of a record's correction history, as the audit view returns it.
/// </summary>
public record AmendmentHistoryEntry(
    Guid AmendmentId,
    Guid AmendmentRequestId,
    string Field,
    string? PreviousValue,
    string? NewValue,
    string Reason,
    AmendmentStatus Status,
    Guid AmendedByRegistrarId,
    string AmendedByRegistrarName,
    DateTime AmendedAtUtc,
    DateTime? AppliedAtUtc,
    Guid? ReviewedByRegistrarId,
    string? ReviewedByRegistrarName,
    DateTime? ReviewedAtUtc,
    string? ReviewNote,
    Guid? TransactionId
);

/// <summary>
/// One flagged conflict, as the registrar's queue lists it.
/// </summary>
public record AmendmentConflictResponse(
    Guid AmendmentConflictId,
    Guid AmendmentRequestId,
    string Brn,
    string ChildFullName,
    Guid FacilityId,
    string Field,

    /// <summary>What the submitting device believed the record said.</summary>
    string? ExpectedPreviousValue,

    /// <summary>What the register actually held when the change arrived.</summary>
    string? ActualPreviousValue,

    /// <summary>
    /// The value that won under last-writer-wins. Null while the change is
    /// still awaiting approval, where nothing has been applied yet.
    /// </summary>
    string? ResolvedValue,

    Guid SubmittedByRegistrarId,
    string SubmittedByRegistrarName,
    DateTime DetectedAtUtc
);

/// <summary>
/// A registrar's judgement on a flagged conflict.
///
/// There is no field here to change the value back. Restoring what the
/// register previously held is an ordinary amendment, and routing it through
/// the amendment path rather than a second write path keeps one code path
/// responsible for previous values, approval rules and certificate
/// withdrawal.
/// </summary>
public record ReviewAmendmentConflictRequest
{
    /// <summary>
    /// True when the resolution stands as it is; false when the registrar has
    /// corrected it by a subsequent amendment.
    /// </summary>
    public bool Uphold { get; init; } = true;

    /// <summary>What was checked, and why the outcome is right. Required.</summary>
    public string Note { get; init; } = string.Empty;
}

public record ReviewAmendmentConflictResponse(
    Guid AmendmentConflictId,
    string Brn,
    AmendmentConflictStatus Status,
    DateTime ReviewedAtUtc
);
