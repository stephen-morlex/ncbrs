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
    string DistrictId,
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
    DateTime? ConfirmedAtUtc = null
);
