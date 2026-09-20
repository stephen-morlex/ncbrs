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
/// W3. Resolving the people behind the ids that queues and histories carry.
///
/// The load-bearing claims are the boundaries, not the listing: the directory
/// stops at the caller's district, a registrar outside it is indistinguishable
/// from one that does not exist, and nothing here ever hands out a PIN hash
/// or a Keycloak subject.
/// </summary>
public class RegistrarDirectoryTests : IDisposable
{
    private static readonly Guid TerekekaFacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid JubaFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");

    private static readonly Guid NurseId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid OfficerId = Guid.Parse("0199a1b2-1003-7000-8000-000000000003");
    private static readonly Guid MinistryId = Guid.Parse("0199a1b2-1004-7000-8000-000000000004");
    private static readonly Guid JubaNurseId = Guid.Parse("0199a1b2-1005-7000-8000-000000000005");

    private const string TerekekaDistrict = "SS-CE-TER";
    private const string JubaDistrict = "SS-CE-JUB";
    private const string OfficerSubject = "33333333-3333-4333-8333-333333333333";
    private const string MinistrySubject = "44444444-4444-4444-8444-444444444444";

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public RegistrarDirectoryTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = TerekekaFacilityId, Name = "Terekeka Village Health Post", DistrictId = TerekekaDistrict },
            new Facility { FacilityId = JubaFacilityId, Name = "Juba Central", DistrictId = JubaDistrict });

        db.Registrars.AddRange(
            new Registrar
            {
                RegistrarId = NurseId,
                FacilityId = TerekekaFacilityId,
                ExternalSubjectId = AuthTestContext.DefaultSubject,
                DisplayName = "Nurse A. Lado",
                CredentialHash = "a-pin-hash-that-must-never-be-published"
            },
            new Registrar
            {
                RegistrarId = OfficerId,
                FacilityId = TerekekaFacilityId,
                ExternalSubjectId = OfficerSubject,
                DisplayName = "Nyandeng Wani",
                Role = RegistrarRole.DistrictOfficer
            },
            new Registrar
            {
                RegistrarId = MinistryId,
                FacilityId = TerekekaFacilityId,
                ExternalSubjectId = MinistrySubject,
                DisplayName = "Aluel Lako",
                Role = RegistrarRole.MinistryAdmin
            },
            new Registrar
            {
                RegistrarId = JubaNurseId,
                FacilityId = JubaFacilityId,
                ExternalSubjectId = "55555555-5555-4555-8555-555555555555",
                DisplayName = "Thandi Nkosi"
            });

        db.SaveChanges();
    }

    // ---- the boundary -----------------------------------------------------

    [Fact]
    public async Task The_directory_stops_at_the_callers_district()
    {
        var page = Ok(await ListAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer]));

        Assert.All(page.Items, entry => Assert.Equal(TerekekaDistrict, entry.DistrictId));
        Assert.DoesNotContain(page.Items, entry => entry.DisplayName == "Thandi Nkosi");
    }

    [Fact]
    public async Task The_total_is_the_scoped_total()
    {
        // A national total beside a district list would say "3 of 4" and
        // disclose that someone exists elsewhere.
        var page = Ok(await ListAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer]));

        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task The_ministry_sees_every_district()
    {
        var page = Ok(await ListAsync(MinistrySubject, [NcbrsRoles.MinistryAdmin]));

        Assert.Equal(4, page.Total);
        Assert.Contains(page.Items, entry => entry.DistrictId == JubaDistrict);
    }

    [Fact]
    public async Task Naming_another_district_is_refused_rather_than_narrowed()
    {
        var result = await ListAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer],
            districtId: JubaDistrict);

        AssertStatus(StatusCodes.Status403Forbidden, result.Result);
    }

    // ---- resolving one id -------------------------------------------------

    [Fact]
    public async Task Any_provisioned_caller_can_put_a_name_to_an_id_in_their_district()
    {
        // This is the whole point of W3: a facility registrar reading that
        // "0199a1b2-…" approved their correction can audit nothing; the same
        // row naming a district officer can be questioned.
        var entry = OkOne(await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], OfficerId));

        Assert.Equal("Nyandeng Wani", entry.DisplayName);
        Assert.Equal(RegistrarRole.DistrictOfficer, entry.Role);
        Assert.Equal("Terekeka Village Health Post", entry.FacilityName);
    }

    [Fact]
    public async Task A_registrar_in_another_district_is_not_found_rather_than_forbidden()
    {
        // 403 would confirm the id exists somewhere, which is exactly what
        // the scope withholds. 404 says only that the caller's district has
        // no such person.
        var result = await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], JubaNurseId);

        AssertStatus(StatusCodes.Status404NotFound, result.Result);
    }

    [Fact]
    public async Task An_unknown_id_is_also_not_found()
    {
        // Same answer as the out-of-district case, deliberately: the two must
        // be indistinguishable or the difference is the disclosure.
        var result = await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], Guid.NewGuid());

        AssertStatus(StatusCodes.Status404NotFound, result.Result);
    }

    [Fact]
    public async Task An_account_with_no_registrar_record_gets_nothing()
    {
        var result = await GetAsync("66666666-6666-4666-8666-666666666666",
            [NcbrsRoles.FacilityRegistrar], OfficerId);

        AssertStatus(StatusCodes.Status403Forbidden, result.Result);
    }

    // ---- what is published ------------------------------------------------

    [Fact]
    public async Task A_directory_entry_never_carries_the_pin_hash_or_the_keycloak_subject()
    {
        // The PIN hash has one legitimate destination -- the credential
        // bundle a device caches for offline verification -- and no business
        // in a directory. The subject identifies the account to Keycloak, not
        // to anyone here, and publishing it invites callers to key their own
        // records on it.
        var entry = OkOne(await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], NurseId));

        var published = entry.GetType().GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain("CredentialHash", published);
        Assert.DoesNotContain("ExternalSubjectId", published);
        Assert.Equal(
            ["RegistrarId", "DisplayName", "Role", "FacilityId", "FacilityName", "DistrictId"],
            published);
    }

    // ---- filtering and paging ---------------------------------------------

    [Fact]
    public async Task A_name_fragment_narrows_the_directory()
    {
        var page = Ok(await ListAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], name: "Nyandeng"));

        var entry = Assert.Single(page.Items);

        Assert.Equal("Nyandeng Wani", entry.DisplayName);
    }

    [Fact]
    public async Task Entries_are_ordered_by_name()
    {
        var page = Ok(await ListAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer]));

        Assert.Equal(["Aluel Lako", "Nurse A. Lado", "Nyandeng Wani"],
            page.Items.Select(entry => entry.DisplayName));
    }

    [Fact]
    public async Task Paging_does_not_repeat_or_skip_an_entry()
    {
        var first = Ok(await ListAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], limit: 2));

        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = Ok(await ListAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer],
            limit: 2, after: first.NextCursor));

        var seen = first.Items.Concat(second.Items).Select(entry => entry.DisplayName).ToList();

        Assert.Equal(["Aluel Lako", "Nurse A. Lado", "Nyandeng Wani"], seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    private async Task<ActionResult<Page<RegistrarResponse>>> ListAsync(
        string subject,
        string[] roles,
        string? name = null,
        string? districtId = null,
        int limit = PageRequest.DefaultLimit,
        string? after = null)
    {
        await using var db = new NcbrsDbContext(_options);

        var http = AuthTestContext.HttpContextFor(subject, roles);

        return await Controller(db, http).List(name, facilityId: null, districtId, limit, after);
    }

    private async Task<ActionResult<RegistrarResponse>> GetAsync(
        string subject,
        string[] roles,
        Guid registrarId)
    {
        await using var db = new NcbrsDbContext(_options);

        var http = AuthTestContext.HttpContextFor(subject, roles);

        return await Controller(db, http).Get(registrarId);
    }

    private static RegistrarsController Controller(NcbrsDbContext db, HttpContext http) =>
        new(db, AuthTestContext.RegistrarService(db, http), new DistrictScopeResolver(new DistrictLookup(db)))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

    private static Page<RegistrarResponse> Ok(ActionResult<Page<RegistrarResponse>> result) =>
        Assert.IsType<Page<RegistrarResponse>>(result.Value);

    private static RegistrarResponse OkOne(ActionResult<RegistrarResponse> result) =>
        Assert.IsType<RegistrarResponse>(result.Value);

    private static void AssertStatus(int expected, ActionResult? result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);

        Assert.Equal(expected, objectResult.StatusCode);
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
