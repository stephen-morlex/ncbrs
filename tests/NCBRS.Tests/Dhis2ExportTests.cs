using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Models;
using NCBRS.Consumer.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The DHIS2 aggregate export (plan E4).
///
/// Its exit condition is a privacy property — "export contains no
/// record-level identifiers" — so that is what these pin, along with the
/// harder half nobody writes down: **aggregation is not anonymity.** A count
/// of one, in a small area, for a rare event, identifies a family. The rarest
/// events in a birth registry are a stillbirth and a mother who died, which
/// makes the small-cell problem here a disclosure about the worst day of
/// someone's life.
/// </summary>
public class Dhis2ExportTests : IDisposable
{
    private const string Period = "202609";
    private const string District = "SS-CE-TER";
    private const string OrgUnit = "OU-CENTRAL-07";

    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ReadModelDbContext> _options;

    public Dhis2ExportTests()
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

    private static Dhis2ExportOptions Options(int minimumCellSize = 5) => new()
    {
        LiveBirths = "UID-LIVE",
        LiveBirthsMale = "UID-LIVE-M",
        LiveBirthsFemale = "UID-LIVE-F",
        FetalDeaths = "UID-FETAL",
        NeonatalDeaths = "UID-NEONATAL",
        MaternalDeaths = "UID-MATERNAL",
        RegisteredWithinWindow = "UID-ON-TIME",
        MinimumCellSize = minimumCellSize,
        OrgUnits = { [District] = OrgUnit }
    };

    private async Task<Dhis2Export> ExportAsync(Dhis2ExportOptions? options = null)
    {
        await using var db = NewDb();

        return await new Dhis2ExportService(db, options ?? Options()).ExportAsync(Period);
    }

    private async Task GivenBirthsAsync(
        int count,
        string sex = "Female",
        string districtId = District,
        string vitalEventType = "LiveBirth",
        bool? withinWindow = true,
        string brnPrefix = "1000")
    {
        await using var db = NewDb();

        for (var index = 0; index < count; index++)
        {
            db.RegistrationFacts.Add(new RegistrationFact
            {
                Brn = $"{brnPrefix}{index:D3}",
                BirthRecordId = Guid.CreateVersion7(),
                CountyCode = districtId,
                FacilityId = FacilityId,
                DateOfBirth = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
                Sex = sex,
                RegisteredAtUtc = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc),
                PublishedAtUtc = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc),
                VitalEventType = vitalEventType,
                WithinStatutoryWindow = withinWindow,
                ConfirmedAtUtc = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc)
            });
        }

        await db.SaveChangesAsync();
    }

    private static string? ValueOf(Dhis2Export export, string dataElement)
        => export.DataValueSet.DataValues
            .SingleOrDefault(value => value.DataElement == dataElement)?.Value;

    // --- the exit condition -------------------------------------------------------

    /// <summary>
    /// The plan's exit condition, checked against the serialised payload
    /// rather than the object graph — because what leaves the building is the
    /// JSON, and a field added to a DTO later would ship without anyone
    /// rewriting a test that only inspected properties.
    /// </summary>
    [Fact]
    public async Task TheExportCarriesNoRecordLevelIdentifiers()
    {
        await GivenBirthsAsync(10, sex: "Female");
        await GivenBirthsAsync(10, sex: "Male", brnPrefix: "2000");

        var json = JsonSerializer.Serialize(await ExportAsync());

        // The BRNs, the record ids and the facility that appear in the read
        // model must appear nowhere here.
        Assert.DoesNotContain("1000000", json);
        Assert.DoesNotContain("2000000", json);
        Assert.DoesNotContain(FacilityId.ToString(), json);

        // Nor any date finer than the reporting period.
        Assert.DoesNotContain("2026-09-10", json);
        Assert.Contains("202609", json);
    }

    /// <summary>
    /// Nothing in the payload is keyed by anything but a district-month, so
    /// there is no row that could describe one birth.
    /// </summary>
    [Fact]
    public async Task EveryValueIsKeyedByOrgUnitAndPeriodOnly()
    {
        await GivenBirthsAsync(10);

        var export = await ExportAsync();

        Assert.NotEmpty(export.DataValueSet.DataValues);
        Assert.All(export.DataValueSet.DataValues, value =>
        {
            Assert.Equal(OrgUnit, value.OrgUnit);
            Assert.Equal(Period, value.Period);
        });
    }

    // --- aggregation is not anonymity ------------------------------------------------

    /// <summary>
    /// A district with a handful of births is withheld entirely — not
    /// published as small cells. Suppressing individual figures while naming
    /// the district would still narrow every one of them to a few families.
    /// </summary>
    [Fact]
    public async Task ADistrictWithTooFewBirthsIsWithheldEntirely()
    {
        await GivenBirthsAsync(3);

        var export = await ExportAsync();

        Assert.Empty(export.DataValueSet.DataValues);

        var suppression = Assert.Single(export.Suppressed);
        Assert.Equal(OrgUnit, suppression.OrgUnit);
        Assert.Contains("Fewer than 5", suppression.Reason);
    }

    /// <summary>
    /// The subtraction attack, and the reason a breakdown goes whole or not
    /// at all: publishing 20 live births and 18 male tells the reader there
    /// were 2 female, however carefully the female cell was suppressed.
    /// </summary>
    [Fact]
    public async Task ABreakdownWithOneSmallCellIsWithheldWhole()
    {
        await GivenBirthsAsync(18, sex: "Male");
        await GivenBirthsAsync(2, sex: "Female", brnPrefix: "2000");

        var export = await ExportAsync();

        // The total is safe on its own and still goes.
        Assert.Equal("20", ValueOf(export, "UID-LIVE"));

        // Neither half of the breakdown does.
        Assert.Null(ValueOf(export, "UID-LIVE-M"));
        Assert.Null(ValueOf(export, "UID-LIVE-F"));

        Assert.Contains(export.Suppressed, entry => entry.Reason.Contains("recoverable"));
    }

    [Fact]
    public async Task ABreakdownWhereEveryCellClearsTheThresholdIsPublished()
    {
        await GivenBirthsAsync(10, sex: "Male");
        await GivenBirthsAsync(10, sex: "Female", brnPrefix: "2000");

        var export = await ExportAsync();

        Assert.Equal("20", ValueOf(export, "UID-LIVE"));
        Assert.Equal("10", ValueOf(export, "UID-LIVE-M"));
        Assert.Equal("10", ValueOf(export, "UID-LIVE-F"));
    }

    /// <summary>
    /// Timeliness decomposes live births just as sex does, so the same rule
    /// applies: publishing "18 of 20 on time" states that 2 were late.
    /// </summary>
    [Fact]
    public async Task TimelinessIsTreatedAsABreakdownNotAStandaloneFigure()
    {
        await GivenBirthsAsync(18, withinWindow: true);
        await GivenBirthsAsync(2, withinWindow: false, brnPrefix: "2000");

        var export = await ExportAsync();

        Assert.Null(ValueOf(export, "UID-ON-TIME"));
        Assert.Contains(export.Suppressed, entry => entry.Reason.Contains("timeliness"));
    }

    /// <summary>
    /// A stillbirth count of one is a disclosure about a specific family.
    /// Rare events are withheld on their own, without taking the district's
    /// other figures with them.
    /// </summary>
    [Fact]
    public async Task ASingleFetalDeathIsWithheldWhileTheRestOfTheDistrictIsPublished()
    {
        await GivenBirthsAsync(20);
        await GivenBirthsAsync(1, vitalEventType: "FetalDeath", brnPrefix: "9000");

        var export = await ExportAsync();

        Assert.Equal("20", ValueOf(export, "UID-LIVE"));
        Assert.Null(ValueOf(export, "UID-FETAL"));
    }

    /// <summary>
    /// And the zeros are withheld too. If a true zero were published and only
    /// the small counts suppressed, every absent value would mean "at least
    /// one" — which is the disclosure the suppression was for.
    /// </summary>
    [Fact]
    public async Task ATrueZeroIsAbsentJustAsASuppressedCountIs()
    {
        await GivenBirthsAsync(20);

        var export = await ExportAsync();

        Assert.Null(ValueOf(export, "UID-FETAL"));
        Assert.Null(ValueOf(export, "UID-NEONATAL"));
        Assert.Null(ValueOf(export, "UID-MATERNAL"));
    }

    [Fact]
    public async Task ARareEventAboveTheThresholdIsPublished()
    {
        await GivenBirthsAsync(40);

        await using (var db = NewDb())
        {
            for (var index = 0; index < 6; index++)
            {
                db.NeonatalOutcomeFacts.Add(new NeonatalOutcomeFact
                {
                    Brn = $"1000{index:D3}",
                    FacilityId = FacilityId,
                    DeathDateUtc = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc),
                    IcdPmTiming = "Neonatal",
                    IcdPmCauseCode = "P21.0",
                    DaysAfterBirth = 5,
                    RecordedAtUtc = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc)
                });
            }

            await db.SaveChangesAsync();
        }

        var export = await ExportAsync();

        Assert.Equal("6", ValueOf(export, "UID-NEONATAL"));
    }

    // --- what is counted ---------------------------------------------------------------

    /// <summary>
    /// An annulment says there was no such birth. Sending one to DHIS2 would
    /// report a birth the register has withdrawn.
    /// </summary>
    [Fact]
    public async Task AnnulledRegistrationsAreNotExported()
    {
        await GivenBirthsAsync(20);

        await using (var db = NewDb())
        {
            foreach (var fact in await db.RegistrationFacts.Take(10).ToListAsync())
            {
                fact.AnnulledAtUtc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
            }

            await db.SaveChangesAsync();
        }

        Assert.Equal("10", ValueOf(await ExportAsync(), "UID-LIVE"));
    }

    [Fact]
    public async Task BirthsOutsideTheReportingMonthAreNotCounted()
    {
        await GivenBirthsAsync(20);

        await using (var db = NewDb())
        {
            foreach (var fact in await db.RegistrationFacts.Take(5).ToListAsync())
            {
                fact.DateOfBirth = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);
            }

            await db.SaveChangesAsync();
        }

        Assert.Equal("15", ValueOf(await ExportAsync(), "UID-LIVE"));
    }

    // --- configuration --------------------------------------------------------------------

    /// <summary>
    /// A district with no DHIS2 mapping is reported, not skipped quietly. Its
    /// births would otherwise never reach the national figures and nothing
    /// would say so.
    /// </summary>
    [Fact]
    public async Task ADistrictWithNoOrgUnitMappingIsReportedRatherThanDropped()
    {
        await GivenBirthsAsync(20, districtId: "D-UNMAPPED-99");

        var export = await ExportAsync();

        Assert.Empty(export.DataValueSet.DataValues);
        Assert.Equal("D-UNMAPPED-99", Assert.Single(export.Unmapped));
    }

    /// <summary>
    /// An unconfigured data element is skipped rather than invented: DHIS2
    /// rejects an unknown UID at best, and files the value against the wrong
    /// indicator at worst.
    /// </summary>
    [Fact]
    public async Task AFigureWithNoConfiguredDataElementIsNotExported()
    {
        await GivenBirthsAsync(20);

        var options = Options();
        options.LiveBirths = null;

        var export = await ExportAsync(options);

        // The total is gone because it has nowhere to be filed; the figures
        // that do have a UID are unaffected.
        Assert.Null(ValueOf(export, "UID-LIVE"));
        Assert.Equal("20", ValueOf(export, "UID-LIVE-F"));
    }

    [Fact]
    public async Task AMalformedPeriodIsRefused()
    {
        await using var db = NewDb();
        var service = new Dhis2ExportService(db, Options());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync("2026-09"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync("202613"));
    }
}
