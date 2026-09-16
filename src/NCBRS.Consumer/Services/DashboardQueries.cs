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
