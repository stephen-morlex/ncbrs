using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Models;
using NCBRS.Consumer.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The §10 indicators, read off the projection (plan F2/F3).
///
/// What most of these pin is not arithmetic but the difference between zero
/// and unknown. A dashboard that reports 0 neonatal deaths because it holds
/// no outcome events tells a Ministry the month went well; one that reports
/// "not available" sends someone to find out. Every indicator here is
/// nullable for that reason, and these tests exist to stop a well-meaning
/// "?? 0" being added later.
/// </summary>
public class DashboardReadModelTests : IDisposable
{
    private static readonly DateTime Now = new(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime PeriodFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodTo = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");

    /// <summary>
    /// "Still filling" and "days silent" are both measured against now, so
    /// the tests pin now rather than letting the calendar decide whether they
    /// pass.
    /// </summary>
    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ReadModelDbContext> _options;
    private readonly TimeProvider _clock = new FixedClock(Now);

    public DashboardReadModelTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ReadModelDbContext>().UseSqlite(_connection).Options;

        using var db = new ReadModelDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ReadModelDbContext NewDb() => new(_options);

    private DashboardQueryService Dashboard(ReadModelDbContext db) => new(db, _clock);

    private async Task<DashboardSummary> SummaryAsync(string? districtId = null)
    {
        await using var db = NewDb();

        return await Dashboard(db).SummaryAsync(PeriodFrom, PeriodTo, districtId);
    }

    private async Task GivenAsync(params object[] facts)
    {
        await using var db = NewDb();

        db.AddRange(facts);
        await db.SaveChangesAsync();
    }

    private static RegistrationFact Birth(
        string brn,
        string districtId = "SS-CE-TER",
        string sex = "Female",
        string? tier = "VillageHealthPost",
        string? vitalEventType = "LiveBirth",
        bool? withinWindow = true,
        int? confirmedAfterDays = 2,
        DateTime? dateOfBirth = null,
        DateTime? annulledAtUtc = null,

        // The two ends of the journey, named separately because they are
        // separate. Null registeredAfterDays is an event published before the
        // stream carried the device's registration time -- a third answer,
        // not a same-day registration.
        int? registeredAfterDays = 1,
        int publishedAfterDays = 1)
    {
        var born = dateOfBirth ?? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        return new RegistrationFact
        {
            Brn = brn,
            BirthRecordId = Guid.CreateVersion7(),
            DistrictId = districtId,
            FacilityId = FacilityId,
            DateOfBirth = born,
            Sex = sex,
            PublishedAtUtc = born.AddDays(publishedAfterDays),
            RegisteredAtUtc = registeredAfterDays is { } captured ? born.AddDays(captured) : null,
            FacilityTier = tier,
            VitalEventType = vitalEventType,
            WithinStatutoryWindow = withinWindow,
            ConfirmedAtUtc = confirmedAfterDays is { } days ? born.AddDays(days) : null,
            AnnulledAtUtc = annulledAtUtc
        };
    }

    private static SyncBatchFact Sync(
        string deviceId,
        string districtId = "SS-CE-TER",
        int submitted = 10,
        int registered = 9,
        int duplicates = 1,
        int rejected = 0,
        DateTime? syncedAtUtc = null)
        => new()
        {
            SyncBatchId = Guid.CreateVersion7(),
            DeviceId = deviceId,
            FacilityId = FacilityId,
            DistrictId = districtId,
            Submitted = submitted,
            Registered = registered,
            Duplicates = duplicates,
            Rejected = rejected,
            Status = "Reconciled",
            SyncedAtUtc = syncedAtUtc ?? new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)
        };

    // --- zero is not unknown ---------------------------------------------------

    /// <summary>
    /// The single most important behaviour here. An empty projection must not
    /// answer "0 deaths, 100% on time" -- that is a report of a good month,
    /// made out of no data at all.
    /// </summary>
    [Fact]
    public async Task AnEmptyProjection_ReportsUnknownRatherThanZero()
    {
        var summary = await SummaryAsync();

        Assert.Equal(0, summary.Registrations.LiveBirths);

        Assert.Null(summary.Registrations.SexRatio);
        Assert.Null(summary.Timeliness.WithinWindowShare);
        Assert.Null(summary.TimeToConfirmation.MedianDays);
        Assert.Null(summary.Mortality.NeonatalDeathsPerThousandLiveBirths);
        Assert.Null(summary.Mortality.MaternalDeathsPerHundredThousandLiveBirths);
        Assert.Null(summary.Duplicates.PerTenThousandBirths);
        Assert.Null(summary.Sync.RegisteredShare);
    }

    /// <summary>
    /// Events published before the indicator fields existed cannot say
    /// whether a birth was on time. Counting them as on time would inflate
    /// the headline figure with records that never reported one.
    /// </summary>
    [Fact]
    public async Task RegistrationsThatCannotSayWhetherTheyWereOnTime_AreCountedSeparately()
    {
        await GivenAsync(
            Birth("100001", withinWindow: true),
            Birth("100002", withinWindow: null),
            Birth("100003", withinWindow: null));

        var summary = await SummaryAsync();

        Assert.Equal(1, summary.Timeliness.WithinWindow);
        Assert.Equal(0, summary.Timeliness.OutsideWindow);
        Assert.Equal(2, summary.Timeliness.Unknown);

        // 1 of 1 known, not 1 of 3 and not 3 of 3.
        Assert.Equal(100m, summary.Timeliness.WithinWindowShare);
    }

    [Fact]
    public async Task TimelinessIsTheShareOfRegistrationsThatCouldBeJudged()
    {
        await GivenAsync(
            Birth("100001", withinWindow: true),
            Birth("100002", withinWindow: true),
            Birth("100003", withinWindow: true),
            Birth("100004", withinWindow: false));

        var summary = await SummaryAsync();

        Assert.Equal(75m, summary.Timeliness.WithinWindowShare);
    }

    /// <summary>
    /// Timeliness is not coverage, and the summary says so rather than
    /// leaving a reader to assume the registry knows about births it has
    /// never seen.
    /// </summary>
    [Fact]
    public async Task TheSummaryStatesThatCompletenessIsNotBeingReported()
    {
        var summary = await SummaryAsync();

        Assert.Contains(summary.NotAvailable, note => note.Contains("completeness", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(summary.NotAvailable, note => note.Contains("Certificate turnaround"));
    }

    // --- counting --------------------------------------------------------------

    [Fact]
    public async Task LiveBirthsAndSexRatioAreCounted()
    {
        await GivenAsync(
            Birth("100001", sex: "Male"),
            Birth("100002", sex: "Male"),
            Birth("100003", sex: "Female"),
            Birth("100004", sex: "Female"));

        var summary = await SummaryAsync();

        Assert.Equal(4, summary.Registrations.LiveBirths);
        Assert.Equal(2, summary.Registrations.Male);
        Assert.Equal(2, summary.Registrations.Female);
        Assert.Equal(100m, summary.Registrations.SexRatio);
    }

    /// <summary>
    /// An annulment says there was no such birth. Counting one would report
    /// a birth the register has withdrawn -- but the count of them is real
    /// and reported, not a zero nobody computed.
    /// </summary>
    [Fact]
    public async Task AnnulledRegistrationsAreExcludedFromEveryIndicatorAndCountedOnTheirOwn()
    {
        await GivenAsync(
            Birth("100001"),
            Birth("100002", annulledAtUtc: Now, withinWindow: false));

        var summary = await SummaryAsync();

        Assert.Equal(1, summary.Registrations.LiveBirths);
        Assert.Equal(1, summary.Registrations.Annulled);

        // The annulled one was outside the window; it must not drag timeliness.
        Assert.Equal(100m, summary.Timeliness.WithinWindowShare);
    }

    /// <summary>
    /// Stillbirth registration does not exist yet, so every event says
    /// LiveBirth or says nothing. The split is carried anyway: the day fetal
    /// death registration lands, a projection without it starts counting
    /// stillbirths as live births and every rate built on that is wrong.
    /// </summary>
    [Fact]
    public async Task FetalDeathsAreNotCountedAsLiveBirths()
    {
        await GivenAsync(
            Birth("100001", vitalEventType: "LiveBirth"),
            Birth("100002", vitalEventType: "FetalDeath"),
            Birth("100003", vitalEventType: null));

        var summary = await SummaryAsync();

        Assert.Equal(1, summary.Registrations.FetalDeaths);

        // The unknown one counts as a live birth for now -- nothing else can
        // be registered -- but it is reported so the assumption is visible.
        Assert.Equal(2, summary.Registrations.LiveBirths);
        Assert.Equal(1, summary.Registrations.VitalEventTypeUnknown);
    }

    [Fact]
    public async Task BirthsAreCountedByDateOfOccurrenceNotByWhenTheyReachedTheCentre()
    {
        // Registered late, well inside the reporting period by date of birth.
        await GivenAsync(Birth(
            "100001",
            dateOfBirth: new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc),
            confirmedAfterDays: 200));

        // And one born outside it.
        await GivenAsync(Birth(
            "100002",
            dateOfBirth: new DateTime(2025, 12, 20, 0, 0, 0, DateTimeKind.Utc)));

        var summary = await SummaryAsync();

        Assert.Equal(1, summary.Registrations.LiveBirths);
    }

    // --- time to registration ---------------------------------------------------

    [Fact]
    public async Task TimeToConfirmationIsAMedianBrokenOutByFacilityTier()
    {
        await GivenAsync(
            Birth("100001", tier: "Hospital", confirmedAfterDays: 1),
            Birth("100002", tier: "Hospital", confirmedAfterDays: 3),
            Birth("100003", tier: "VillageHealthPost", confirmedAfterDays: 20),
            Birth("100004", tier: "VillageHealthPost", confirmedAfterDays: 30));

        var summary = await SummaryAsync();

        var hospital = summary.TimeToConfirmation.ByFacilityTier.Single(tier => tier.FacilityTier == "Hospital");
        var post = summary.TimeToConfirmation.ByFacilityTier.Single(tier => tier.FacilityTier == "VillageHealthPost");

        Assert.Equal(2m, hospital.MedianDays);
        Assert.Equal(25m, post.MedianDays);
    }

    /// <summary>
    /// The draft asks for a median rather than a mean, and this is why: one
    /// birth registered a decade late would drag an average to a number
    /// describing nobody.
    /// </summary>
    [Fact]
    public async Task OneVeryLateRegistrationDoesNotDragTheMedian()
    {
        await GivenAsync(
            Birth("100001", confirmedAfterDays: 1),
            Birth("100002", confirmedAfterDays: 2),
            Birth("100003", confirmedAfterDays: 3),
            Birth("100004", confirmedAfterDays: 4),
            Birth("100005", confirmedAfterDays: 4000));

        var summary = await SummaryAsync();

        Assert.Equal(3m, summary.TimeToConfirmation.MedianDays);
    }

    /// <summary>
    /// A provisional record has no confirmed BRN, so it has no
    /// time-to-registration. Treating it as zero days would make the offline
    /// tier look faster the more of its records were stuck.
    /// </summary>
    [Fact]
    public async Task UnconfirmedRecordsAreExcludedFromTheMedianAndReported()
    {
        await GivenAsync(
            Birth("100001", confirmedAfterDays: 4),
            Birth("PROV-TABLET07-1", confirmedAfterDays: null),
            Birth("PROV-TABLET07-2", confirmedAfterDays: null));

        var summary = await SummaryAsync();

        Assert.Equal(1, summary.TimeToConfirmation.Confirmed);
        Assert.Equal(2, summary.TimeToConfirmation.StillUnconfirmed);
        Assert.Equal(4m, summary.TimeToConfirmation.MedianDays);
    }

    // --- mortality ---------------------------------------------------------------

    [Fact]
    public async Task MortalityRatesUseLiveBirthsAsTheDenominator()
    {
        var births = Enumerable.Range(1, 500)
            .Select(index => Birth($"1005{index:D2}00"))
            .Cast<object>()
            .ToArray();

        await GivenAsync(births);

        await GivenAsync(new NeonatalOutcomeFact
        {
            Brn = "100501" + "00",
            FacilityId = FacilityId,
            DeathDateUtc = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            IcdPmTiming = "Neonatal",
            IcdPmCauseCode = "N1",
            DaysAfterBirth = 9,
            RecordedAtUtc = Now
        });

        var summary = await SummaryAsync();

        Assert.Equal(500, summary.Registrations.LiveBirths);
        Assert.Equal(1, summary.Mortality.NeonatalDeaths);
        Assert.Equal(2m, summary.Mortality.NeonatalDeathsPerThousandLiveBirths);
    }

    /// <summary>
    /// A death recorded against a birth outside the reporting period must not
    /// land in it, or a district's mortality rate reflects deaths its
    /// denominator never counted.
    /// </summary>
    [Fact]
    public async Task ADeathAgainstABirthOutsideThePeriodIsNotCounted()
    {
        await GivenAsync(
            Birth("100001"),
            Birth("900001", dateOfBirth: new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc)));

        await GivenAsync(new MaternalOutcomeFact
        {
            Brn = "900001",
            FacilityId = FacilityId,
            DeathDateUtc = new DateTime(2025, 5, 3, 0, 0, 0, DateTimeKind.Utc),
            IcdMmCauseCode = "M3",
            DaysAfterBirth = 2,
            RecordedAtUtc = Now
        });

        var summary = await SummaryAsync();

        Assert.Equal(0, summary.Mortality.MaternalDeaths);
    }

    // --- sync and duplicates -------------------------------------------------------

    [Fact]
    public async Task SyncReliabilityCountsBatchesAndDistinctDevices()
    {
        await GivenAsync(
            Sync("TABLET-01", submitted: 10, registered: 9, rejected: 1),
            Sync("TABLET-01", submitted: 5, registered: 5, rejected: 0),
            Sync("TABLET-02", submitted: 5, registered: 4, rejected: 1));

        var summary = await SummaryAsync();

        Assert.Equal(3, summary.Sync.Batches);
        Assert.Equal(2, summary.Sync.DevicesReporting);
        Assert.Equal(20, summary.Sync.RecordsSubmitted);
        Assert.Equal(18, summary.Sync.RecordsRegistered);
        Assert.Equal(2, summary.Sync.RecordsRejected);
        Assert.Equal(90m, summary.Sync.RegisteredShare);
    }

    /// <summary>
    /// The duplicate rate is a floor, and says so. Duplicates refused on the
    /// direct online path are announced to nobody, so a reader must not take
    /// this for the whole rate.
    /// </summary>
    [Fact]
    public async Task TheDuplicateRateDeclaresThatItOnlySeesTheSyncPath()
    {
        await GivenAsync(Birth("100001"), Birth("100002"));
        await GivenAsync(Sync("TABLET-01", duplicates: 1));

        var summary = await SummaryAsync();

        Assert.Equal(1, summary.Duplicates.DuplicatesSeen);
        Assert.Equal(5000m, summary.Duplicates.PerTenThousandBirths);
        Assert.Contains("floor", summary.Duplicates.Caveat);
    }

    // --- drill-down -----------------------------------------------------------------

    [Fact]
    public async Task TheNationalViewDrillsDownByDistrict()
    {
        await GivenAsync(
            Birth("100001", districtId: "SS-CE-TER"),
            Birth("100002", districtId: "SS-CE-TER"),
            Birth("100003", districtId: "SS-CE-TER", annulledAtUtc: Now),
            Birth("200001", districtId: "SS-CE-JUB"));

        await using var db = NewDb();
        var districts = await Dashboard(db).DistrictsAsync(PeriodFrom, PeriodTo);

        var central = districts.Single(district => district.DistrictId == "SS-CE-TER");

        Assert.Equal(2, central.LiveBirths);
        Assert.Equal(1, central.Annulled);
        Assert.Equal(1, districts.Single(district => district.DistrictId == "SS-CE-JUB").LiveBirths);
    }

    [Fact]
    public async Task ASummaryCanBeScopedToOneDistrict()
    {
        await GivenAsync(
            Birth("100001", districtId: "SS-CE-TER"),
            Birth("200001", districtId: "SS-CE-JUB"),
            Birth("200002", districtId: "SS-CE-JUB"));

        var summary = await SummaryAsync("SS-CE-JUB");

        Assert.Equal("SS-CE-JUB", summary.DistrictId);
        Assert.Equal(2, summary.Registrations.LiveBirths);
    }

    // --- the period itself ------------------------------------------------------------

    /// <summary>
    /// A recent period is still filling: births inside it are still being
    /// registered. Presenting one as final is how a dashboard shows a
    /// collapse in births every month and a recovery every quarter.
    /// </summary>
    [Fact]
    public async Task ARecentPeriodIsMarkedAsStillFilling()
    {
        await using var db = NewDb();

        var recent = await Dashboard(db).SummaryAsync(Now.AddDays(-30), Now);
        var settled = await Dashboard(db).SummaryAsync(
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(recent.Period.StillFilling);
        Assert.False(settled.Period.StillFilling);
    }

    // --- devices that have gone quiet ----------------------------------------------------

    [Fact]
    public async Task ADeviceThatHasStoppedSyncingIsReported()
    {
        await GivenAsync(
            Sync("TABLET-QUIET", syncedAtUtc: Now.AddDays(-30)),
            Sync("TABLET-BUSY", syncedAtUtc: Now.AddDays(-1)));

        await using var db = NewDb();
        var silent = await Dashboard(db).SilentDevicesAsync(silentForDays: 7);

        var quiet = Assert.Single(silent);

        Assert.Equal("TABLET-QUIET", quiet.DeviceId);
        Assert.Equal(30, quiet.DaysSilent);
        Assert.Equal("SS-CE-TER", quiet.DistrictId);
    }

    /// <summary>
    /// Silence is measured from a device's most recent sync, not its first.
    /// Otherwise a device that has been reporting for a year looks like one
    /// that stopped a year ago.
    /// </summary>
    [Fact]
    public async Task SilenceIsMeasuredFromTheMostRecentSync()
    {
        await GivenAsync(
            Sync("TABLET-01", syncedAtUtc: Now.AddDays(-200)),
            Sync("TABLET-01", syncedAtUtc: Now.AddDays(-2)));

        await using var db = NewDb();

        Assert.Empty(await Dashboard(db).SilentDevicesAsync(silentForDays: 7));
    }

    [Fact]
    public async Task SilentDevicesCanBeScopedToOneDistrict()
    {
        await GivenAsync(
            Sync("TABLET-CENTRAL", districtId: "SS-CE-TER", syncedAtUtc: Now.AddDays(-30)),
            Sync("TABLET-LUSAKA", districtId: "SS-CE-JUB", syncedAtUtc: Now.AddDays(-30)));

        await using var db = NewDb();
        var silent = await Dashboard(db).SilentDevicesAsync(silentForDays: 7, districtId: "SS-CE-JUB");

        Assert.Equal("TABLET-LUSAKA", Assert.Single(silent).DeviceId);
    }

    // ---- Registration delay -------------------------------------------
    //
    // Two delays sit between a birth and a national figure: how long the
    // family took to reach a registrar, and how long the record then took to
    // reach the centre. One is answered by a health campaign, the other by a
    // mast. TimeToConfirmation spans both and can separate neither.

    [Fact]
    public async Task TheDelayBeforeRegistration_IsReportedApartFromTheDelayReachingTheCentre()
    {
        // Registered two days after the birth, and published 30 days after
        // the birth -- so 28 of those days were the post waiting for a link,
        // and only 2 were the family.
        await GivenAsync(
            Birth("100001", registeredAfterDays: 2, publishedAfterDays: 30),
            Birth("100002", registeredAfterDays: 2, publishedAfterDays: 30));

        var summary = await SummaryAsync();

        Assert.Equal(2, summary.RegistrationDelay.Measured);
        Assert.Equal(2m, summary.RegistrationDelay.MedianDaysBirthToRegistration);
        Assert.Equal(28m, summary.RegistrationDelay.MedianDaysRegistrationToCentre);
    }

    [Fact]
    public async Task AHospitalAndAVillagePost_AreNotAveragedTogether()
    {
        // The whole reason for the tier breakdown. A hospital terminal's
        // second figure is zero by construction; the village post's is three
        // weeks. A single national median would describe neither, and would
        // move whenever the mix of facilities changed rather than when
        // anything about the country did.
        await GivenAsync(
            Birth("100001", tier: "Hospital", registeredAfterDays: 1, publishedAfterDays: 1),
            Birth("100002", tier: "VillageHealthPost", registeredAfterDays: 1, publishedAfterDays: 22));

        var summary = await SummaryAsync();

        var hospital = summary.RegistrationDelay.ByFacilityTier.Single(t => t.FacilityTier == "Hospital");
        var post = summary.RegistrationDelay.ByFacilityTier.Single(t => t.FacilityTier == "VillageHealthPost");

        Assert.Equal(0m, hospital.MedianDaysRegistrationToCentre);
        Assert.Equal(21m, post.MedianDaysRegistrationToCentre);

        // The families were equally prompt in both places, which is exactly
        // the fact a combined figure would have hidden.
        Assert.Equal(1m, hospital.MedianDaysBirthToRegistration);
        Assert.Equal(1m, post.MedianDaysBirthToRegistration);
    }

    [Fact]
    public async Task EventsPredatingTheField_AreCountedAndExcluded_NotTreatedAsNoDelay()
    {
        // The rule this whole projection is built on: an indicator whose
        // inputs are unknown says so. Folding these in as zero-day delays
        // would report an offline tier getting faster the more of its history
        // predated the measurement.
        await GivenAsync(
            Birth("100001", registeredAfterDays: 4, publishedAfterDays: 10),
            Birth("100002", registeredAfterDays: null, publishedAfterDays: 10),
            Birth("100003", registeredAfterDays: null, publishedAfterDays: 10));

        var summary = await SummaryAsync();

        Assert.Equal(1, summary.RegistrationDelay.Measured);
        Assert.Equal(2, summary.RegistrationDelay.NotMeasurable);

        // The median is of the one record that can answer, not of three.
        Assert.Equal(4m, summary.RegistrationDelay.MedianDaysBirthToRegistration);
        Assert.Equal(6m, summary.RegistrationDelay.MedianDaysRegistrationToCentre);
    }

    [Fact]
    public async Task WhenNothingCanBeMeasured_TheMediansAreNullAndNotZero()
    {
        // A Ministry reading "0 days" concludes the offline tier is
        // instantaneous. A Ministry reading "not available" asks why.
        await GivenAsync(
            Birth("100001", registeredAfterDays: null),
            Birth("100002", registeredAfterDays: null));

        var summary = await SummaryAsync();

        Assert.Equal(0, summary.RegistrationDelay.Measured);
        Assert.Equal(2, summary.RegistrationDelay.NotMeasurable);
        Assert.Null(summary.RegistrationDelay.MedianDaysBirthToRegistration);
        Assert.Null(summary.RegistrationDelay.MedianDaysRegistrationToCentre);
    }

    [Fact]
    public async Task ADeviceClockAheadOfTheServer_DoesNotProduceANegativeWait()
    {
        // The registry accepts a device clock up to the skew tolerance ahead
        // of the server, so an online registration can be published fractally
        // "before" it was registered. Left unfloored, that reports the centre
        // receiving births before they happened.
        await GivenAsync(
            Birth("100001", registeredAfterDays: 3, publishedAfterDays: 2),
            Birth("100002", registeredAfterDays: 3, publishedAfterDays: 2));

        var summary = await SummaryAsync();

        Assert.Equal(0m, summary.RegistrationDelay.MedianDaysRegistrationToCentre);
    }

    // --- trends over time ------------------------------------------------------

    private async Task<IReadOnlyList<TrendPoint>> TrendsAsync(string? districtId = null)
    {
        await using var db = NewDb();

        return await Dashboard(db).TrendsAsync(PeriodFrom, PeriodTo, districtId);
    }

    private static TrendPoint MonthOf(IReadOnlyList<TrendPoint> trends, int month)
        => trends.Single(point => point.Period.FromUtc.Year == 2026 && point.Period.FromUtc.Month == month);

    private static DateTime OnDay(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Trends_BucketBirthsByMonthOfOccurrence()
    {
        await GivenAsync(
            Birth("100001", dateOfBirth: OnDay(6, 10)),
            Birth("100002", dateOfBirth: OnDay(6, 20)),
            Birth("100003", dateOfBirth: OnDay(8, 5)));

        var trends = await TrendsAsync();

        // A bucket per calendar month across the default year, so a chart has
        // an unbroken axis rather than only the months that had births.
        Assert.Equal(12, trends.Count);
        Assert.Equal(2, MonthOf(trends, 6).LiveBirths);
        Assert.Equal(1, MonthOf(trends, 8).LiveBirths);
        Assert.Equal(0, MonthOf(trends, 7).LiveBirths);
    }

    [Fact]
    public async Task Trends_ExcludeAnnulledFromTheLineButCountThem()
    {
        await GivenAsync(
            Birth("100001", dateOfBirth: OnDay(6, 10)),
            Birth("100002", dateOfBirth: OnDay(6, 12), annulledAtUtc: Now));

        var june = MonthOf(await TrendsAsync(), 6);

        Assert.Equal(1, june.LiveBirths);
        Assert.Equal(1, june.Annulled);
    }

    [Fact]
    public async Task Trends_MarkTheMostRecentBucketAsStillFilling()
    {
        // The point of carrying StillFilling per bucket: a line must be able to
        // mark the month that is simply not over, not draw it as a fall.
        var trends = await TrendsAsync();

        Assert.True(MonthOf(trends, 12).Period.StillFilling);
        Assert.False(MonthOf(trends, 2).Period.StillFilling);
    }

    [Fact]
    public async Task Trends_ShareIsNullNotZeroWhenNoBirthKnowsItsWindow()
    {
        // A month with births none of which report a window status is a gap in
        // the line — "not available" — not a plunge to zero.
        await GivenAsync(Birth("100001", dateOfBirth: OnDay(6, 10), withinWindow: null));

        Assert.Null(MonthOf(await TrendsAsync(), 6).WithinWindowShare);
    }
}
