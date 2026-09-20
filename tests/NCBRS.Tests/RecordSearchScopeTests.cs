using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// W1's scoping, at the controller, where it is actually decided.
///
/// The service takes a scope and honours it; these are the tests of how that
/// scope is arrived at. That is the security boundary: everything below it
/// trusts the answer, so a mistake here is not caught anywhere else.
/// </summary>
public class RecordSearchScopeTests : IDisposable
{
    private static readonly Guid TerekekaFacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid JubaFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid MinistryId = Guid.Parse("0199a1b2-1002-7000-8000-000000000002");

    private const string TerekekaDistrict = "SS-CE-TER";
    private const string JubaDistrict = "SS-CE-JUB";
    private const string MinistrySubject = "33333333-3333-4333-8333-333333333333";
    private const string StrangerSubject = "44444444-4444-4444-8444-444444444444";

    private static readonly DateTime Born = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public RecordSearchScopeTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = TerekekaFacilityId, Name = "Terekeka Village Health Post", CountyCode = TerekekaDistrict },
            new Facility { FacilityId = JubaFacilityId, Name = "Juba Central", CountyCode = JubaDistrict });

        db.Registrars.AddRange(
            new Registrar
            {
                RegistrarId = RegistrarId,
                FacilityId = TerekekaFacilityId,
                ExternalSubjectId = AuthTestContext.DefaultSubject,
                DisplayName = "Nurse A. Lado"
            },
            new Registrar
            {
                RegistrarId = MinistryId,
                FacilityId = TerekekaFacilityId,
                ExternalSubjectId = MinistrySubject,
                DisplayName = "Ministry Admin",
                Role = RegistrarRole.MinistryAdmin
            });

        db.BirthRecords.AddRange(
            Record("100002", TerekekaFacilityId, "Aluel Lado"),
            Record("200001", JubaFacilityId, "Nyandeng Lado"));

        db.SaveChanges();
    }

    [Fact]
    public async Task A_registrar_searching_their_own_district_is_allowed()
    {
        var result = await SearchAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar],
            districtId: TerekekaDistrict);

        var page = Assert.IsType<Page<BirthRecordSearchHit>>(Assert.IsType<ActionResult<Page<BirthRecordSearchHit>>>(result).Value);

        Assert.Single(page.Items);
        Assert.Equal("100002", page.Items[0].Brn);
    }

    [Fact]
    public async Task Naming_another_district_is_refused_rather_than_quietly_narrowed()
    {
        // The refusal is the point. Narrowing to their own district instead
        // would return an empty page, which the caller reads as "no such
        // child in Juba" -- a false answer about a district they were never
        // allowed to ask about.
        var result = await SearchAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar],
            districtId: JubaDistrict);

        AssertStatus(StatusCodes.Status403Forbidden, result);
    }

    [Fact]
    public async Task A_district_officer_is_not_exempt()
    {
        // Overseeing several facilities is not overseeing several districts.
        // Only the Ministry is national.
        var result = await SearchAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.DistrictOfficer],
            districtId: JubaDistrict);

        AssertStatus(StatusCodes.Status403Forbidden, result);
    }

    [Fact]
    public async Task The_ministry_may_search_another_district()
    {
        var result = await SearchAsync(MinistrySubject, [NcbrsRoles.MinistryAdmin],
            districtId: JubaDistrict);

        var page = Assert.IsType<Page<BirthRecordSearchHit>>(Assert.IsType<ActionResult<Page<BirthRecordSearchHit>>>(result).Value);

        Assert.Single(page.Items);
        Assert.Equal("200001", page.Items[0].Brn);
    }

    [Fact]
    public async Task The_ministry_naming_no_district_searches_nationally()
    {
        var result = await SearchAsync(MinistrySubject, [NcbrsRoles.MinistryAdmin]);

        var page = Assert.IsType<Page<BirthRecordSearchHit>>(Assert.IsType<ActionResult<Page<BirthRecordSearchHit>>>(result).Value);

        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task A_registrar_naming_no_district_gets_their_own_not_the_nation()
    {
        // Omitting the parameter must not be a way to widen the scope. This
        // is the failure that would look like a working search.
        var result = await SearchAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar]);

        var page = Assert.IsType<Page<BirthRecordSearchHit>>(Assert.IsType<ActionResult<Page<BirthRecordSearchHit>>>(result).Value);

        Assert.Single(page.Items);
        Assert.Equal(TerekekaDistrict, page.Items[0].DistrictId);
    }

    [Fact]
    public async Task An_account_with_no_registrar_record_cannot_search()
    {
        // Authenticated, possibly holding a realm role, but never provisioned
        // here -- so there is nobody for the trail to name, and an
        // unattributable search is the one thing this endpoint must refuse.
        var result = await SearchAsync(StrangerSubject, [NcbrsRoles.FacilityRegistrar]);

        AssertStatus(StatusCodes.Status403Forbidden, result);
    }

    [Fact]
    public async Task A_search_with_no_criteria_is_refused()
    {
        var result = await SearchAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar],
            name: null);

        AssertStatus(StatusCodes.Status400BadRequest, result);
    }

    [Fact]
    public async Task A_backwards_date_range_is_refused()
    {
        var result = await SearchAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar],
            name: null, bornFrom: Born, bornTo: Born.AddDays(-1));

        AssertStatus(StatusCodes.Status400BadRequest, result);
    }

    [Fact]
    public async Task A_refused_search_writes_no_audit_row()
    {
        // Nothing was read, so nothing was accessed. An audit row here would
        // fill the trail with non-events and make the real ones harder to
        // see.
        await SearchAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar],
            districtId: JubaDistrict);

        await using var db = new NcbrsDbContext(_options);

        Assert.False(await db.AuditLogs.AnyAsync(log => log.EntityType == "BirthRecordSearch"));
    }

    private async Task<ActionResult<Page<BirthRecordSearchHit>>> SearchAsync(
        string subject,
        string[] roles,
        string? name = "Lado",
        DateTime? bornFrom = null,
        DateTime? bornTo = null,
        string? districtId = null)
    {
        await using var db = new NcbrsDbContext(_options);

        var http = AuthTestContext.HttpContextFor(subject, roles);

        var controller = new RecordSearchController(
            db,
            new RecordSearchService(db, TimeProvider.System),
            AuthTestContext.RegistrarService(db, http),
            new CountyScopeResolver(new CountyLookup(db)))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        return await controller.Search(name, bornFrom, bornTo, districtId: districtId);
    }

    private static void AssertStatus(int expected, ActionResult<Page<BirthRecordSearchHit>> result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);

        Assert.Equal(expected, objectResult.StatusCode);
    }

    private static BirthRecord Record(string brn, Guid facilityId, string childName)
        => new()
        {
            Brn = brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = childName },
            FacilityId = facilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = Born,
            Sex = Sex.Female,
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
