namespace NCBRS.Models;

/// <summary>
/// A request to void a registration outright.
///
/// There is deliberately no field to un-annul. If an annulment was itself a
/// mistake the remedy is a fresh registration, which leaves both acts visible
/// -- not an edit that makes the withdrawal disappear.
/// </summary>
public record AnnulRecordRequest
{
    public AnnulmentReason Reason { get; init; }

    /// <summary>
    /// The full justification. Held to a higher bar than an amendment's
    /// reason: this is the entire basis for withdrawing a legal identity.
    /// </summary>
    public string Justification { get; init; } = string.Empty;

    /// <summary>
    /// The court order, ministerial direction or investigation reference
    /// authorising this. Required when the reason is CourtOrdered.
    /// </summary>
    public string? AuthorityReference { get; init; }
}

public record AnnulRecordResponse(
    string Brn,
    RecordStatus Status,
    AnnulmentReason Reason,
    string? AuthorityReference,

    /// <summary>
    /// True when a valid certificate existed and was revoked. It is published
    /// to the revocation list, so a verifier checking the printed document
    /// will refuse it.
    /// </summary>
    bool CertificateRevoked,

    DateTime AnnulledAtUtc
);

/// <summary>
/// The annulment standing of one record, as the record view reports it, so a
/// BRN that has circulated still resolves to an explanation.
/// </summary>
public record AnnulmentSummary(
    AnnulmentReason Reason,
    string Justification,
    string? AuthorityReference,
    DateTime AnnulledAtUtc
);
