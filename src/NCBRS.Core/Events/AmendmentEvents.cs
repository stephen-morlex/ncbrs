namespace NCBRS.Events;

/// <summary>One field corrected on a registered birth.</summary>
public record AmendedField(string Field, string? PreviousValue, string? NewValue);

/// <summary>
/// Published to "ncbrs.birth-records.amended".
///
/// Downstream consumers hold copies of a birth that are now stale --
/// statistics already counted, a National ID record already pushed. They
/// need the correction, and they need to know what changed rather than just
/// that something did, so the previous values travel with it.
/// </summary>
public record BirthRecordAmendedEvent(
    string Brn,
    Guid BirthRecordId,
    Guid FacilityId,
    IReadOnlyList<AmendedField> Changes,
    string Reason,
    Guid AmendedByRegistrarId,

    /// <summary>
    /// True when the amendment touched a field the certificate's signature
    /// covers, so any certificate already issued no longer matches the
    /// record.
    /// </summary>
    bool CertificateInvalidated,

    DateTime EventTimestampUtc,
    Guid? TransactionId
);

/// <summary>
/// Published to "ncbrs.sync.audit".
///
/// The chain-of-custody stream (design decision #5): which device sent what,
/// when, and what became of it. Kept as an event rather than only a table so
/// the audit archive can be built and rebuilt independently of the
/// registration database -- and so a district can notice a village post that
/// has stopped syncing at all.
/// </summary>
public record SyncBatchProcessedEvent(
    Guid SyncBatchId,
    string DeviceId,
    Guid FacilityId,
    [property: System.Text.Json.Serialization.JsonPropertyName("DistrictId")] string County,
    Guid? UploadedByRegistrarId,
    int Submitted,
    int Registered,
    int Duplicates,
    int Rejected,
    string Status,
    DateTime EventTimestampUtc,
    Guid? TransactionId
);

/// <summary>
/// Published to "ncbrs.birth-records.annulled".
///
/// A topic of its own rather than a variant of ".amended", and deliberately
/// so: the two carry opposite instructions. A consumer handling an amendment
/// updates its copy of the record; a consumer handling an annulment must void
/// it. Folding annulment into the amendment stream would leave a National ID
/// record standing for an identity the register has withdrawn -- which is the
/// single worst outcome this event exists to prevent.
///
/// This topic is an addition to the list in draft 6.4.1, which predates the
/// annulment path.
/// </summary>
public record BirthRecordAnnulledEvent(
    string Brn,
    Guid BirthRecordId,
    Guid FacilityId,

    /// <summary>Coded: RegisteredInError, FraudulentRegistration, CourtOrdered.</summary>
    string Reason,

    string Justification,
    string? AuthorityReference,
    Guid AnnulledByRegistrarId,

    /// <summary>True when a valid certificate was revoked by the annulment.</summary>
    bool CertificateRevoked,

    DateTime EventTimestampUtc,
    Guid? TransactionId
);
