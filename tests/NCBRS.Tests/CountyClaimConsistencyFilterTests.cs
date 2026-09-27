using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Web;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// An officer's county is recorded twice: the registry places their facility,
/// and the identity provider places them in a county group that reporting is
/// scoped by. The API is the one service that can see both, so it refuses a
/// token that contradicts the registry -- otherwise an officer moved to another
/// county's facility, whose group nobody changed, would act on one county's
/// records while reading another's figures, and nothing would say so.
/// </summary>
public class CountyClaimConsistencyFilterTests : IDisposable
{
    private static readonly Guid JubaHospital = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private const string Subject = "officer";

    private readonly TestDatabase _database = TestDatabase.Create();

    public CountyClaimConsistencyFilterTests()
    {
        using var db = new NcbrsDbContext(_database.Options);
        db.Facilities.Add(new Facility
        {
            FacilityId = JubaHospital, Name = "Juba Teaching Hospital", CountyCode = "SS0101",
            BrnBlockStart = 100_000, BrnBlockEnd = 199_999, BrnBlockNextAvailable = 100_000,
        });
        db.Registrars.Add(new Registrar
        {
            FacilityId = JubaHospital, ExternalSubjectId = Subject, DisplayName = "Nyandeng Wani",
            Role = RegistrarRole.DistrictOfficer, CredentialHash = "x",
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <returns>Whether the action ran, and the refusal if it did not.</returns>
    private async Task<(bool Ran, IActionResult? Result)> RunAsync(string role, params string[] groups)
    {
        await using var db = new NcbrsDbContext(_database.Options);

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Subject), new(ClaimTypes.Role, role) };
        claims.AddRange(groups.Select(group => new Claim(KeycloakCounties.GroupsClaim, group)));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role)),
        };

        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ControllerActionDescriptor()),
            [], new Dictionary<string, object?>(), controller: null!);

        var ran = false;
        var filter = new CountyClaimConsistencyFilter(AuthTestContext.RegistrarService(db, http), new CountyLookup(db));

        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller: null!));
        });

        return (ran, context.Result);
    }

    private static int? StatusOf(IActionResult? result) => (result as ObjectResult)?.StatusCode;

    [Fact]
    public async Task AnOfficerWhoseCountiesAgreeProceeds()
        => Assert.True((await RunAsync(NcbrsRoles.DistrictOfficer, "/counties/SS0101")).Ran);

    /// <summary>The drift this exists for: moved facility, stale group.</summary>
    [Fact]
    public async Task AnOfficerWhoseCountiesDisagreeIsRefused()
    {
        var (ran, result) = await RunAsync(NcbrsRoles.DistrictOfficer, "/counties/SS0105");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
    }

    [Fact]
    public async Task AnOfficerInTwoCountyGroupsIsRefused()
    {
        var (ran, result) = await RunAsync(NcbrsRoles.DistrictOfficer, "/counties/SS0101", "/counties/SS0105");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
    }

    /// <summary>
    /// No group at all is left to the reporting service to refuse, with its own
    /// message: the API scopes from the registry and needs no claim.
    /// </summary>
    [Fact]
    public async Task AnOfficerWithNoCountyGroupIsNotTheApisToRefuse()
        => Assert.True((await RunAsync(NcbrsRoles.DistrictOfficer)).Ran);

    /// <summary>Only district officers are placed by county; nobody else is checked.</summary>
    [Fact]
    public async Task OtherRolesAreNotChecked()
        => Assert.True((await RunAsync(NcbrsRoles.FacilityRegistrar, "/counties/SS0105")).Ran);
}
