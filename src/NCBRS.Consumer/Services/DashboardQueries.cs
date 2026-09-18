namespace NCBRS.Consumer.Services;

/// <summary>
/// The window a figure covers, and whether that figure can be treated as
/// settled.
/// </summary>
/// <param name="StillFilling">
/// True when the period ends recently enough that registrations for births
/// inside it are still arriving -- a post offline for three weeks, or a
/// family registering on day 80 of a 90-day window.
///
/// Reporting a recent month as though it were final is the standard way a
/// civil registration dashboard misleads: the figure only ever rises, so
/// every fresh month looks like a collapse in births and every older one
/// looks like a recovery.
/// </param>
public record ReportingPeriod(DateTime FromUtc, DateTime ToUtc, bool StillFilling);

/// <summary>
/// Births counted by date of occurrence, which is how vital statistics are
/// tabulated (UN P&amp;R Rev. 3) -- not by when the registration reached the
/// centre, which is an artefact of connectivity.
/// </summary>
public record RegistrationCounts(
    int LiveBirths,
    int FetalDeaths,

    /// <summary>
    /// Registrations from events predating the vital-event-type field. They
    /// are almost certainly live births -- nothing else can be registered
    /// yet -- but they are reported separately rather than assumed, because
    /// the moment stillbirth registration exists this number stops being
    /// harmless.
    /// </summary>
    int VitalEventTypeUnknown,

    int Annulled,
    int Male,
    int Female,

    /// <summary>
    /// Males per 100 females. Null when there are no female live births:
    /// a ratio with an empty denominator is not infinity, it is unknown.
    /// </summary>
    decimal? SexRatio);

/// <summary>
/// The share of registrations made inside the statutory window.
///
/// **This is timeliness, not completeness**, and the difference is not
/// pedantry. Completeness asks what share of births that happened were
/// registered at all; its denominator is projected births from a census or
/// demographic model, which no registry holds and this one cannot invent.
/// Reporting timeliness under the word "completeness" would tell a Ministry
/// its coverage is 96% when the true figure might be half that -- and the
/// births it would be wrong about are exactly the ones in the places the
/// offline tier exists to reach.
/// </summary>
public record Timeliness(
    int WithinWindow,
    int OutsideWindow,

    /// <summary>Registrations from events predating the field. Never folded into either side.</summary>
    int Unknown,

    /// <summary>Null when nothing in the period has a known window status.</summary>
    decimal? WithinWindowShare);

/// <summary>
/// Days from birth to a BRN the centre has reconciled against the block it
/// granted. Provisional records are excluded and counted, not treated as
/// zero-day or as missing.
/// </summary>
public record TimeToConfirmation(
    int Confirmed,
    int StillUnconfirmed,
    decimal? MedianDays,
    IReadOnlyList<TierTimeToConfirmation> ByFacilityTier);

public record TierTimeToConfirmation(string FacilityTier, int Confirmed, decimal? MedianDays);

/// <summary>
/// The two delays between a birth happening and the centre knowing about it,
/// reported separately because they are different problems with different
/// remedies.
///
/// **Birth to registration** is how long the family took to reach a
/// registrar: an outreach question, answered by a health campaign or a
/// mobile clinic. **Registration to centre** is how long the record then
/// waited for a link: a connectivity question, answered by a mast or a
/// better sync schedule. Added together they are a number that points at
/// neither, and for the offline tier the second can dwarf the first.
///
/// Broken out by tier because that is where they diverge most: a hospital
/// terminal's second figure is zero by construction, and a village post's
/// may be weeks.
/// </summary>
public record RegistrationDelay(
    /// <summary>Registrations whose event carried the device's registration time.</summary>
    int Measured,

    /// <summary>
    /// Registrations published before the event carried it. Reported rather
    /// than dropped: a median over a tenth of the period, presented as though
    /// it covered all of it, is worse than one nobody trusts.
    /// </summary>
    int NotMeasurable,

    decimal? MedianDaysBirthToRegistration,
    decimal? MedianDaysRegistrationToCentre,
    IReadOnlyList<TierRegistrationDelay> ByFacilityTier);

public record TierRegistrationDelay(
    string FacilityTier,
    int Measured,
    decimal? MedianDaysBirthToRegistration,
    decimal? MedianDaysRegistrationToCentre);

/// <summary>
/// Deaths recorded against births in the period. Rates are per 1,000 live
/// births and null when there are none -- the draft's §10 figures are
/// meaningless on an empty denominator and a zero would read as "no deaths".
/// </summary>
public record Mortality(
    int NeonatalDeaths,
    int MaternalDeaths,
    decimal? NeonatalDeathsPerThousandLiveBirths,
    decimal? MaternalDeathsPerHundredThousandLiveBirths);

/// <summary>
/// What the offline tier managed to deliver, counted by sync date rather
/// than by date of birth -- this measures the link, not the births.
/// </summary>
public record SyncReliability(
    int Batches,
    int DevicesReporting,
    int RecordsSubmitted,
    int RecordsRegistered,
    int RecordsRejected,
    decimal? RegisteredShare);

/// <summary>
/// Duplicates per 10,000 births (§10).
/// </summary>
/// <param name="Caveat">
/// Only duplicates caught on the sync path reach the event stream at all. A
/// duplicate rejected on a direct online registration is refused by the API
/// and announced to nobody, so this number is a floor, not the rate.
/// </param>
public record DuplicateRate(int DuplicatesSeen, decimal? PerTenThousandBirths, string Caveat);

/// <summary>A device that has synced before and has not synced lately.</summary>
public record SilentDevice(
    string DeviceId,
    string DistrictId,
    Guid FacilityId,
    DateTime LastSyncAtUtc,
    int DaysSilent);

public record DashboardSummary(
    ReportingPeriod Period,

    /// <summary>Null for the national view.</summary>
    string? DistrictId,

    RegistrationCounts Registrations,
    Timeliness Timeliness,
    TimeToConfirmation TimeToConfirmation,
    RegistrationDelay RegistrationDelay,
    Mortality Mortality,
    SyncReliability Sync,
    DuplicateRate Duplicates,

    /// <summary>
    /// §10 indicators this read model cannot produce, each with the reason.
    /// Carried in the payload so a dashboard renders "not available" rather
    /// than a zero -- an indicator silently reading 0 is worse than an
    /// indicator visibly missing.
    /// </summary>
    IReadOnlyList<string> NotAvailable);

public record DistrictSummary(string DistrictId, int LiveBirths, int Annulled, decimal? WithinWindowShare);

/// <summary>
/// One calendar month of the headline figures, for charting a trend across the
/// year. A compact slice of the summary rather than the whole of it repeated
/// per point.
///
/// The same two rules the summary keeps apply here, and the first matters more
/// on a line than in a tile: <see cref="ReportingPeriod.StillFilling"/> marks
/// the most recent bucket as not settled, so a chart does not draw the month
/// that is simply not over yet as a fall in births. A share is null — "not
/// available" — never zero, so a month with no known window status is a gap in
/// the line, not a plunge to the axis.
/// </summary>
public record TrendPoint(
    ReportingPeriod Period,
    int LiveBirths,
    int FetalDeaths,
    int Annulled,
    decimal? WithinWindowShare,
    int NeonatalDeaths,
    int MaternalDeaths);

/// <summary>
/// The error shape these endpoints return on a bad request.
///
/// A named record rather than an anonymous object, because an anonymous one
/// serialises fine and documents as nothing — leaving the generated client
/// with no type for the half of the contract that reports failure.
/// </summary>
public record ApiError(string Error);

/// <summary>
/// Whether the projection is keeping up.
///
/// Named for the same reason: a dashboard served from a consumer that stalled
/// three days ago looks exactly like a dashboard of a country where nothing
/// happened, so this is a contract worth generating a client for rather than
/// an ad hoc blob.
/// </summary>
public record ProjectionHealth(
    string Status,
    DateTime? LastEventProcessedAtUtc,
    int Registrations,
    int HeldAwaitingRegistration);
