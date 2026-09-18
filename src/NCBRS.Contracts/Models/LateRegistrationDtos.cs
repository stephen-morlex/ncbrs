namespace NCBRS.Models;

/// <summary>A late registration awaiting verification, as the queue lists it.</summary>
public record PendingLateRegistrationResponse(
    Guid LateRegistrationId,
    string Brn,
    string ChildFullName,
    DateTime DateOfBirth,
    int DaysLate,
    int WindowDaysAtFiling,
    LateRegistrationEvidenceType EvidenceType,
    string? EvidenceReference,
    string DeclarantName,
    string DeclarantRelationship,
    Guid FacilityId,
    string FacilityName,
    Guid SubmittedByRegistrarId,
    string SubmittedByRegistrarName,
    DateTime SubmittedAtUtc
);

/// <summary>A district registrar's decision on a late registration.</summary>
public record ReviewLateRegistrationRequest
{
    public bool Approve { get; init; }

    /// <summary>
    /// What was checked, or why it was refused. Required either way: an
    /// unexplained approval is the same as no verification at all, which is
    /// the thing this process exists to prevent.
    /// </summary>
    public string Note { get; init; } = string.Empty;
}

public record ReviewLateRegistrationResponse(
    Guid LateRegistrationId,
    string Brn,
    LateRegistrationStatus Status,
    DateTime ReviewedAtUtc,
    string ReviewNote
);

/// <summary>
/// The late-registration standing of one record, as the registration
/// response and the record view report it.
/// </summary>
public record LateRegistrationSummary(
    int DaysLate,
    int WindowDaysAtFiling,
    LateRegistrationStatus Status,
    LateRegistrationEvidenceType EvidenceType
);
