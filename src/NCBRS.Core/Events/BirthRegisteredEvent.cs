using System.Text.Json.Serialization;

namespace NCBRS.Events;

/// <summary>
/// Published to "ncbrs.birth-records.registered" once a BirthRecord is
/// confirmed centrally. Downstream consumers (National ID Authority push,
/// dashboards, audit archive) react to this instead of being called
/// directly from the registration API -- see Section 6.4.1 of the draft.
/// </summary>
public record BirthRegisteredEvent(
    string Brn,
    Guid BirthRecordId,
    Guid FacilityId,

    /// <summary>
    /// The county the registration is accountable to. The wire name stays
    /// <c>DistrictId</c> (pinned) so events published before this rename still
    /// deserialise on replay — an event contract is not renamed under its
    /// readers, only the code that reads it.
    /// </summary>
    [property: JsonPropertyName("DistrictId")] string County,
    DateTime DateOfBirth,
    string Sex,
    DateTime EventTimestampUtc,
    /// <summary>
    /// The API request that caused this registration. Carried across the
    /// topic so a downstream consumer can trace an event back to the
    /// originating call, the same way RequestLog and AuditLog do.
    /// </summary>
    Guid? TransactionId,

    // The four below exist for the §10 indicators, which cannot be derived
    // from the fields above. Every one is NULLABLE ON PURPOSE: events already
    // in the topic predate them, and a consumer must be able to tell "this
    // birth was on time" from "this event cannot say". Defaulting them to
    // false or LiveBirth would let an unknown quietly count as a known, which
    // is how a national figure ends up wrong in the safe-looking direction.

    /// <summary>
    /// Hospital, Clinic or VillageHealthPost. Time-to-registration is
    /// reported broken out by tier, and the tier is a property of the
    /// facility rather than the birth, so a consumer holding no copy of the
    /// registry cannot look it up.
    /// </summary>
    string? FacilityTier = null,

    /// <summary>
    /// LiveBirth or FetalDeath. Carried even though the registration path
    /// only produces live births today: the moment fetal-death registration
    /// exists, a consumer without this field starts counting stillbirths as
    /// live births, and every rate derived from it is wrong with nothing to
    /// show for it.
    /// </summary>
    string? VitalEventType = null,

    /// <summary>
    /// Whether the registration fell inside the statutory window, as decided
    /// at registration against the device's capture time.
    ///
    /// The decision travels rather than the timestamps, because the window is
    /// configuration that changes by law: recomputing it downstream next year
    /// would silently restate what was on time last year.
    /// </summary>
    bool? WithinStatutoryWindow = null,

    /// <summary>
    /// When the BRN was reconciled against the block the centre granted, or
    /// null while the record is still provisional. This, not the event
    /// timestamp, is the endpoint of "time to registration" -- a record whose
    /// number the centre has not confirmed is not yet a registration anyone
    /// can rely on.
    /// </summary>
    DateTime? ConfirmedAtUtc = null,

    /// <summary>
    /// When the birth was registered on the device, as distinct from
    /// <see cref="EventTimestampUtc"/>, which is when the centre published it.
    ///
    /// **Two different delays sit between a birth and a figure on a national
    /// dashboard, and until this travelled they were one number.** How long a
    /// family took to reach a registrar is a question about outreach; how long
    /// the record then took to arrive is a question about connectivity. The
    /// remedies are a health campaign and a mast, and a figure that adds them
    /// together points at neither.
    ///
    /// Carried as the timestamp rather than as a derived lag — unlike
    /// <see cref="WithinStatutoryWindow"/>, which travels as a decision
    /// because a law can change underneath it. Nothing can change what a
    /// subtraction of two instants means, so a consumer can safely do its own
    /// arithmetic, and the raw pair supports breakdowns nobody has asked for
    /// yet.
    ///
    /// Null on events published before this field existed, which is a third
    /// answer and not a zero-day lag.
    /// </summary>
    DateTime? RegisteredAtUtc = null
);
