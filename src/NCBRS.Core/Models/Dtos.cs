
namespace NCBRS.Models;

/// <summary>
/// What a facility device (online or offline) submits to register a live
/// birth. The device itself is responsible for generating the BRN from its
/// locally reserved block when offline; this DTO carries that BRN through
/// so the server can validate rather than reissue it.
///
/// Field rules live in RegisterBirthRequestValidator, not here.
/// </summary>
public record RegisterBirthRequest
{
    public string Brn { get; init; } = string.Empty;

    public Guid FacilityId { get; init; }

    public string ChildFullName { get; init; } = string.Empty;

    public DateTime DateOfBirth { get; init; }

    public Sex Sex { get; init; }

    public int? BirthWeightGrams { get; init; }

    public decimal? GestationalAgeWeeks { get; init; }

    public BirthPlurality Plurality { get; init; }

    public int? BirthOrder { get; init; }

    public string? MotherFullName { get; init; }

    public string? FatherFullName { get; init; }

    public string DeviceId { get; init; } = string.Empty;

    /// <summary>
    /// When the birth was captured on the device, which is not when it
    /// reached the server.
    ///
    /// The statutory window is measured to this moment. Measuring to arrival
    /// instead would treat every registration from a village post offline for
    /// three weeks as late, routing the whole offline tier through an
    /// evidence-verification process it should never have entered.
    ///
    /// Omitted by an online caller, where the two are the same instant.
    /// </summary>
    public DateTime? RegisteredAtUtc { get; init; }

    /// <summary>
    /// Required when the birth falls outside the statutory window, and
    /// refused when it does not -- supplying it for an on-time birth means
    /// the device and the registry disagree about the date, which is worth
    /// surfacing rather than quietly ignoring.
    /// </summary>
    public LateRegistrationDetails? LateRegistration { get; init; }

    /// <summary>
    /// The statistical questionnaire (UN P&amp;R Rev. 3), optionally captured
    /// in the same workflow as the registration -- which is what draft 6.5.1
    /// describes: legally distinct functions, one facility form.
    ///
    /// Optional, and never able to fail the registration. A questionnaire
    /// that contradicts itself is dropped with the reason audited rather
    /// than taking a real birth down with it.
    /// </summary>
    public MaternalStatisticsRequest? MaternalStatistics { get; init; }
}

/// <summary>
/// The evidence supporting a birth registered after the statutory window
/// (draft Sections 4.1, 5.3).
/// </summary>
public record LateRegistrationDetails
{
    public LateRegistrationEvidenceType EvidenceType { get; init; }

    /// <summary>The card number, register entry or affidavit reference.</summary>
    public string? EvidenceReference { get; init; }

    /// <summary>Who is presenting the claim.</summary>
    public string DeclarantName { get; init; } = string.Empty;

    /// <summary>Their standing to present it -- mother, father, guardian.</summary>
    public string DeclarantRelationship { get; init; } = string.Empty;
}

public record BirthRecordResponse(
    Guid BirthRecordId,
    string Brn,
    string ChildFullName,
    DateTime DateOfBirth,
    Sex Sex,
    RecordStatus Status,

    /// <summary>
    /// Present only when the birth fell outside the statutory window. A
    /// device must show this: the registration succeeded, but no certificate
    /// can be issued until a district registrar verifies the evidence, and a
    /// family should not be sent away expecting one.
    /// </summary>
    LateRegistrationSummary? LateRegistration = null,

    /// <summary>
    /// When the centre reconciled the BRN against a block it granted and
    /// confirmed it as permanent (draft 5.1). Null while the record remains
    /// provisional — which, on a record that registered successfully, means
    /// the number could not be matched to an allocated block and a district
    /// officer will review it.
    /// </summary>
    DateTime? ConfirmedAtUtc = null,

    /// <summary>
    /// Present when the registration has been voided. Returned rather than
    /// hidden: a BRN that has circulated must keep resolving to an
    /// explanation of what became of it.
    /// </summary>
    AnnulmentSummary? Annulment = null
);

/// <summary>
/// The BRN range granted to a facility device by request-brn-block. BlockEnd
/// may be smaller than requested if it was clamped to the facility's
/// pre-approved BrnBlockEnd ceiling.
/// </summary>
public record BrnBlockResponse(
    Guid FacilityId,
    long BlockStart,
    long BlockEnd
);

/// <summary>
/// Body for request-brn-block. Carried as a body rather than query string
/// so the endpoint can take the same { meta, data } envelope as every other
/// call.
/// </summary>
public record BrnBlockRequest
{
    public int BlockSize { get; init; } = 200;

    public string? DeviceId { get; init; }
}

/// <summary>
/// Tracking metadata the caller sends with a request. transactionId must be
/// unique across all requests: replaying one is rejected with 409, so a
/// retry can never be mistaken for a second registration.
/// </summary>
public record RequestMeta
{
    public Guid? TransactionId { get; init; }

    public string? ClientId { get; init; }
}

/// <summary>
/// Lets the meta-promoting action filter find the envelope's metadata
/// without reflecting over the open generic.
/// </summary>
public interface IHasRequestMeta
{
    RequestMeta? Meta { get; }
}

/// <summary>
/// Standard envelope for every request that has a body: tracking metadata
/// alongside the actual payload, mirroring the response shape.
/// </summary>
public record ApiRequest<T> : IHasRequestMeta
{
    public RequestMeta? Meta { get; init; }

    public T Data { get; init; } = default!;
}

/// <summary>
/// Echoed back on every API response so a caller can tie the reply to the
/// request that produced it -- including a request whose response never
/// arrived, which it can then re-query or replay by transaction id.
/// </summary>
public record ResponseMeta(
    Guid TransactionId,
    string? ClientId,
    bool TransactionIdGenerated,
    DateTime TimestampUtc
);

/// <summary>
/// Standard envelope for every response: the tracking metadata alongside
/// the actual payload, rather than mixed into it.
/// </summary>
public record ApiResponse<T>(
    ResponseMeta Meta,
    T Data
);

/// <summary>
/// One field-level problem, named so a client can attach it to the input
/// that caused it rather than showing a wall of text.
/// </summary>
public record ApiError(
    string Field,
    string Message
);

/// <summary>
/// The payload returned in <c>data</c> whenever a request is rejected.
/// Uniform across validation failures, conflicts and not-founds, so a
/// caller parses one error shape.
/// </summary>
public record ApiErrorResponse(
    int Status,
    string Title,
    IReadOnlyList<ApiError> Errors
);
