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
/// W2 at the controller: who may see which facilities, and what a facility
/// says about its own block.
/// </summary>
public class FacilityDirectoryTests : IDisposable
{
    private static readonly Guid PostId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid HospitalId = Guid.Parse("0199a1b2-0003-7000-8000-000000000003");
    private static readonly Guid LusakaId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");

    private static readonly Guid NurseId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid MinistryId = Guid.Parse("0199a1b2-1004-7000-8000-000000000004");

    private const string CentralDistrict = "D-CENTRAL-07";
    private const string LusakaDistrict = "D-LUSAKA-01";
    private const string MinistrySubject = "44444444-4444-4444-8444-444444444444";

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public FacilityDirectoryTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            // 100 left, offline-first: below its 500 threshold.
            new Facility
            {
                FacilityId = PostId,
                Name = "Kabwe Village Health Post",
                DistrictId = CentralDistrict,
                Tier = FacilityTier.VillageHealthPost,
                ConnectivityProfile = ConnectivityProfile.OfflineFirst,
                BrnBlockStart = 1, BrnBlockEnd = 1000, BrnBlockNextAvailable = 901,
            },
            // The same 100 left, always-on: above its 50 threshold.
            new Facility
            {
                FacilityId = HospitalId,
                Name = "Ndola Teaching Hospital",
                DistrictId = CentralDistrict,
                Tier = FacilityTier.Hospital,
                ConnectivityProfile = ConnectivityProfile.AlwaysOn,
                BrnBlockStart = 1, BrnBlockEnd = 1000, BrnBlockNextAvailable = 901,
            },
            new Facility
            {
                FacilityId = LusakaId,
                Name = "Lusaka Central",
                DistrictId = LusakaDistrict,
                Tier = FacilityTier.Hospital,
                ConnectivityProfile = ConnectivityProfile.AlwaysOn,
                BrnBlockStart = 1, BrnBlockEnd = 1000, BrnBlockNextAvailable = 500,
            });

        db.Registrars.AddRange(
            new Registrar { RegistrarId = NurseId, FacilityId = PostId, ExternalSubjectId = AuthTestContext.DefaultSubject, DisplayName = "Nurse A. Banda" },
            new Registrar { RegistrarId = MinistryId, FacilityId = PostId, ExternalSubjectId = MinistrySubject, DisplayName = "Naledi Zulu", Role = RegistrarRole.MinistryAdmin });

        db.SaveChanges();
    }

    // ---- the boundary -----------------------------------------------------

    [Fact]
    public async Task A_caller_sees_only_their_own_districts_facilities()
    {
        var page = Ok(await ListAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar]));

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, facility => Assert.Equal(CentralDistrict, facility.DistrictId));
    }

    [Fact]
    public async Task The_ministry_sees_every_district()
    {
        var page = Ok(await ListAsync(MinistrySubject, [NcbrsRoles.MinistryAdmin]));

        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task A_facility_in_another_district_is_not_found_rather_than_forbidden()
    {
        var result = await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], LusakaId);

        AssertStatus(StatusCodes.Status404NotFound, result.Result);
    }

    [Fact]
    public async Task A_facility_registrar_may_see_their_own_post()
    {
        // Not gated on an oversight role: the registrar handing out slips
        // when the block runs out is the person who most needs the warning.
        var facility = OkOne(await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], PostId));

        Assert.Equal("Kabwe Village Health Post", facility.Name);
    }

    // ---- what a facility says about its block -----------------------------

    [Fact]
    public async Task Two_facilities_with_the_same_numbers_left_get_different_verdicts()
    {
        // The whole point of W2: 100 remaining is comfortable for a hospital
        // and a warning for a post that will be offline when it runs out.
        var page = Ok(await ListAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar]));

        var post = page.Items.Single(facility => facility.FacilityId == PostId);
        var hospital = page.Items.Single(facility => facility.FacilityId == HospitalId);

        Assert.Equal(100, post.BrnRemaining);
        Assert.Equal(100, hospital.BrnRemaining);

        Assert.Equal(BrnBlockStatus.Low, post.BlockStatus);
        Assert.Equal(BrnBlockStatus.Healthy, hospital.BlockStatus);
    }

    [Fact]
    public async Task A_facility_publishes_the_threshold_it_was_judged_against()
    {
        // A status without its threshold looks arbitrary: a reader seeing a
        // post flagged at 400 and a hospital clear at 60 needs to know why.
        var post = OkOne(await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], PostId));
        var hospital = OkOne(await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], HospitalId));

        Assert.Equal(500, post.BrnWarnBelow);
        Assert.Equal(50, hospital.BrnWarnBelow);
    }

    [Fact]
    public async Task The_actual_block_range_is_published_alongside_the_verdict()
    {
        // Someone reconciling a disputed BRN needs the range the facility was
        // granted, which only the numbers answer.
        var post = OkOne(await GetAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar], PostId));

        Assert.Equal(1, post.BrnBlockStart);
        Assert.Equal(1000, post.BrnBlockEnd);
        Assert.Equal(901, post.BrnBlockNextAvailable);
    }

    // ---- filtering --------------------------------------------------------

    [Fact]
    public async Task Filtering_by_block_status_finds_the_facilities_that_need_a_block()
    {
        var page = Ok(await ListAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar],
            blockStatus: BrnBlockStatus.Low));

        var facility = Assert.Single(page.Items);

        Assert.Equal(PostId, facility.FacilityId);
    }

    [Fact]
    public async Task Filtering_by_tier_narrows_to_that_kind_of_facility()
    {
        var page = Ok(await ListAsync(AuthTestContext.DefaultSubject, [NcbrsRoles.FacilityRegistrar],
            tier: FacilityTier.Hospital));

        var facility = Assert.Single(page.Items);

        Assert.Equal(HospitalId, facility.FacilityId);
    }

    private async Task<ActionResult<Page<FacilityResponse>>> ListAsync(
        string subject,
        string[] roles,
        FacilityTier? tier = null,
        BrnBlockStatus? blockStatus = null)
    {
        await using var db = new NcbrsDbContext(_options);
        var http = AuthTestContext.HttpContextFor(subject, roles);

        return await Controller(db, http).List(name: null, tier, blockStatus);
    }

    private async Task<ActionResult<FacilityResponse>> GetAsync(
        string subject, string[] roles, Guid facilityId)
    {
        await using var db = new NcbrsDbContext(_options);
        var http = AuthTestContext.HttpContextFor(subject, roles);

        return await Controller(db, http).Get(facilityId);
    }

    private static FacilitiesController Controller(NcbrsDbContext db, HttpContext http) =>
        new(db, AuthTestContext.RegistrarService(db, http), new DistrictScopeResolver(new DistrictLookup(db)),
            new BrnBlockOptions())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

    private static Page<FacilityResponse> Ok(ActionResult<Page<FacilityResponse>> result) =>
        Assert.IsType<Page<FacilityResponse>>(result.Value);

    private static FacilityResponse OkOne(ActionResult<FacilityResponse> result) =>
        Assert.IsType<FacilityResponse>(result.Value);

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
