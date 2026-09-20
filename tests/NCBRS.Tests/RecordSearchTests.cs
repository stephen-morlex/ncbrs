using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// W1. Searching the register by name and date.
///
/// The load-bearing claims are the controls, not the matching: a search never
/// leaves the caller's district, a search a caller cannot be attributed to
/// never happens, and every search that does happen is written to the trail
/// with what was looked for. The query itself is the easy half.
/// </summary>
public class RecordSearchTests : IDisposable
{
    private static readonly Guid TerekekaFacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid JubaFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private const string TerekekaDistrict = "SS-CE-TER";
    private const string JubaDistrict = "SS-CE-JUB";

    private static readonly DateTime Born2026 = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Born2024 = new(2024, 3, 2, 8, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public RecordSearchTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = TerekekaFacilityId, Name = "Terekeka Village Health Post", CountyCode = TerekekaDistrict },
            new Facility { FacilityId = JubaFacilityId, Name = "Juba Central", CountyCode = JubaDistrict });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = TerekekaFacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Lado"
        });

        db.BirthRecords.AddRange(
            Record("100001", TerekekaFacilityId, "Ayen Deng", Born2026),
            Record("100002", TerekekaFacilityId, "Aluel Lado", Born2026.AddDays(-1)),
            Record("100003", TerekekaFacilityId, "Garang Lado", Born2024),
            // Same surname, different district. The scope test turns on this.
            Record("200001", JubaFacilityId, "Nyandeng Lado", Born2026));

        db.SaveChanges();
    }

    // ---- scope -----------------------------------------------------------

    [Fact]
    public async Task A_district_search_never_returns_another_districts_records()
    {
        // "Lado" matches three children; one of them is in Juba. A
        // registrar in Central must not learn that the Juba child exists.
        var results = await SearchAsync(
            new RecordSearchCriteria { Name = "Lado" },
            SearchScope.District(TerekekaDistrict));

        Assert.Equal(2, results.Items.Count);
        Assert.All(results.Items, hit => Assert.Equal(TerekekaDistrict, hit.DistrictId));
        Assert.DoesNotContain(results.Items, hit => hit.Brn == "200001");
    }

    [Fact]
    public async Task The_total_is_the_scoped_total_not_the_national_one()
    {
        // A total that counted nationally would leak the existence of records
        // the caller may not see -- "2 shown of 3" tells them there is one
        // more Lado somewhere, which is the disclosure the scope prevents.
        var results = await SearchAsync(
            new RecordSearchCriteria { Name = "Lado" },
            SearchScope.District(TerekekaDistrict));

        Assert.Equal(2, results.Total);
    }

    [Fact]
    public async Task The_ministry_searches_nationally()
    {
        var results = await SearchAsync(
            new RecordSearchCriteria { Name = "Lado" },
            SearchScope.National);

        Assert.Equal(3, results.Total);
        Assert.Contains(results.Items, hit => hit.DistrictId == JubaDistrict);
    }

    // ---- what counts as a search -----------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    public void A_blank_or_one_letter_name_is_not_a_search(string? name)
    {
        // Leaving the form blank must not be a way to read a district out of
        // the register, and one letter matches a large fraction of it.
        var criteria = new RecordSearchCriteria { Name = name };

        Assert.False(criteria.IsSpecific);
    }

    [Fact]
    public void A_facility_or_status_alone_is_still_a_bulk_read()
    {
        // "Every birth at this clinic" is not a search for a child.
        var criteria = new RecordSearchCriteria
        {
            FacilityId = TerekekaFacilityId,
            Status = RecordStatus.Confirmed
        };

        Assert.False(criteria.IsSpecific);
    }

    [Fact]
    public void A_date_range_alone_is_a_search()
    {
        // A registrar who has a date but no usable spelling of the name is
        // the case this exists for.
        var criteria = new RecordSearchCriteria { BornFrom = Born2026.AddDays(-7) };

        Assert.True(criteria.IsSpecific);
    }

    // ---- the audit trail -------------------------------------------------

    [Fact]
    public async Task Every_search_is_written_to_the_audit_trail()
    {
        await SearchAsync(
            new RecordSearchCriteria { Name = "Lado" },
            SearchScope.District(TerekekaDistrict));

        await using var db = new NcbrsDbContext(_options);
        var entry = await db.AuditLogs.SingleAsync(log => log.EntityType == "BirthRecordSearch");

        Assert.Equal(RegistrarId, entry.UserId);
        Assert.Equal(TerekekaDistrict, entry.EntityId);
    }

    [Fact]
    public async Task The_trail_records_what_was_searched_for_not_merely_that_a_search_happened()
    {
        // Twelve rows saying "a search occurred" cannot distinguish a
        // registrar helping one family from someone enumerating a district.
        await SearchAsync(
            new RecordSearchCriteria { Name = "Lado", BornFrom = Born2024 },
            SearchScope.District(TerekekaDistrict));

        await using var db = new NcbrsDbContext(_options);
        var entry = await db.AuditLogs.SingleAsync(log => log.EntityType == "BirthRecordSearch");

        Assert.Contains("name=Lado", entry.Action);
        Assert.Contains("from=2024-03-02", entry.Action);
    }

    [Fact]
    public async Task The_trail_records_how_much_was_returned()
    {
        // A search returning one record and a search returning four hundred
        // are different acts, and the difference is only visible if the
        // count is kept.
        await SearchAsync(
            new RecordSearchCriteria { Name = "Lado" },
            SearchScope.District(TerekekaDistrict));

        await using var db = new NcbrsDbContext(_options);
        var entry = await db.AuditLogs.SingleAsync(log => log.EntityType == "BirthRecordSearch");

        Assert.Contains("returned=2", entry.Action);
        Assert.Contains("total=2", entry.Action);
    }

    [Fact]
    public async Task A_search_that_matches_nothing_is_still_audited()
    {
        // Especially this one. Repeated fruitless searches for a surname are
        // what fishing looks like, and an audit that only recorded hits would
        // be blind to exactly that.
        var results = await SearchAsync(
            new RecordSearchCriteria { Name = "Nobody" },
            SearchScope.District(TerekekaDistrict));

        Assert.Empty(results.Items);

        await using var db = new NcbrsDbContext(_options);
        var entry = await db.AuditLogs.SingleAsync(log => log.EntityType == "BirthRecordSearch");

        Assert.Contains("name=Nobody", entry.Action);
        Assert.Contains("returned=0", entry.Action);
    }

    [Fact]
    public async Task A_national_search_is_audited_as_national()
    {
        await SearchAsync(new RecordSearchCriteria { Name = "Lado" }, SearchScope.National);

        await using var db = new NcbrsDbContext(_options);
        var entry = await db.AuditLogs.SingleAsync(log => log.EntityType == "BirthRecordSearch");

        Assert.Equal("national", entry.EntityId);
    }

    // ---- results ---------------------------------------------------------

    [Fact]
    public async Task A_date_range_excludes_births_outside_it()
    {
        var results = await SearchAsync(
            new RecordSearchCriteria { BornFrom = Born2026.AddDays(-2), BornTo = Born2026.AddDays(1) },
            SearchScope.District(TerekekaDistrict));

        Assert.Equal(2, results.Total);
        Assert.DoesNotContain(results.Items, hit => hit.Brn == "100003");
    }

    [Fact]
    public async Task Results_are_newest_birth_first()
    {
        // A registrar helping a family is far more often looking for a recent
        // registration than an old one.
        var results = await SearchAsync(
            new RecordSearchCriteria { BornFrom = Born2024.AddYears(-1) },
            SearchScope.District(TerekekaDistrict));

        Assert.Equal(["100001", "100002", "100003"], results.Items.Select(hit => hit.Brn));
    }

    [Fact]
    public async Task A_result_carries_only_what_identifies_the_right_child()
    {
        // Enough to recognise them and open the record; no parents, no
        // weights, nothing a result list would spread across every search
        // that happened to match.
        var results = await SearchAsync(
            new RecordSearchCriteria { Name = "Ayen" },
            SearchScope.District(TerekekaDistrict));

        var hit = Assert.Single(results.Items);

        Assert.Equal("100001", hit.Brn);
        Assert.Equal("Ayen Deng", hit.ChildFullName);
        Assert.Equal(Sex.Female, hit.Sex);
        Assert.Equal("Terekeka Village Health Post", hit.FacilityName);
    }

    [Fact]
    public async Task Paging_does_not_repeat_or_skip_a_record()
    {
        var first = await SearchAsync(
            new RecordSearchCriteria { BornFrom = Born2024.AddYears(-1) },
            SearchScope.District(TerekekaDistrict),
            new PageRequest { Limit = 2 });

        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = await SearchAsync(
            new RecordSearchCriteria { BornFrom = Born2024.AddYears(-1) },
            SearchScope.District(TerekekaDistrict),
            new PageRequest { Limit = 2, After = first.NextCursor });

        var seen = first.Items.Concat(second.Items).Select(hit => hit.Brn).ToList();

        Assert.Equal(["100001", "100002", "100003"], seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    private async Task<Page<BirthRecordSearchHit>> SearchAsync(
        RecordSearchCriteria criteria,
        SearchScope scope,
        PageRequest? page = null)
    {
        await using var db = new NcbrsDbContext(_options);

        var service = new RecordSearchService(db, TimeProvider.System);

        var results = await service.SearchAsync(
            criteria,
            scope,
            page ?? new PageRequest(),
            new AuditContext(RegistrarId, "web", null));

        // The service stages the audit row and leaves saving to the caller,
        // so the write stays in the caller's transaction.
        await db.SaveChangesAsync();

        return results;
    }

    private static BirthRecord Record(string brn, Guid facilityId, string childName, DateTime bornAt)
        => new()
        {
            Brn = brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = childName },
            FacilityId = facilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = bornAt,
            Sex = childName.StartsWith("Garang", StringComparison.Ordinal) ? Sex.Male : Sex.Female,
            BirthWeightGrams = 3200,
            Plurality = BirthPlurality.Singleton,
            Status = RecordStatus.Confirmed
        };

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
