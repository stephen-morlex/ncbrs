using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Web;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers who the API thinks you are and what it lets you touch. These
/// guard the hole that existed before Keycloak: the acting registrar used to
/// be whatever id the request body claimed, so any caller could file a birth
/// as anyone, at any facility.
/// </summary>
public class AuthorizationTests : IDisposable
{
    private static readonly Guid HomeFacility = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid OtherFacility = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public AuthorizationTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.AddRange(
            new Facility
            {
                FacilityId = HomeFacility,
                Name = "Kabwe Village Health Post",
                DistrictId = "D-CENTRAL-07"
            },
            new Facility
            {
                FacilityId = OtherFacility,
                Name = "Lusaka Central Hospital",
                DistrictId = "D-LUSAKA-01"
            });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = HomeFacility,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Banda",
            Role = RegistrarRole.FacilityRegistrar,
            CredentialHash = "test"
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    [Fact]
    public async Task TheCallerResolves_FromTheTokenSubject()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();

        var registrar = await AuthTestContext.RegistrarService(db, http).GetAsync();

        Assert.NotNull(registrar);
        Assert.Equal(RegistrarId, registrar.RegistrarId);
    }

    /// <summary>
    /// An account that authenticated but was never provisioned in the
    /// registry resolves to nobody, and the controllers turn that into a 403
    /// rather than writing a record with no author.
    /// </summary>
    [Fact]
    public async Task AnUnprovisionedSubject_ResolvesToNobody()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(subject: Guid.NewGuid().ToString());

        Assert.Null(await AuthTestContext.RegistrarService(db, http).GetAsync());
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_ResolvesToNobody()
    {
        await using var db = NewDb();

        Assert.Null(await AuthTestContext.RegistrarService(db, new DefaultHttpContext()).GetAsync());
    }

    [Fact]
    public async Task AFacilityRegistrar_MayActForTheirOwnFacility()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.True(service.CanActForFacility(registrar!, HomeFacility));
    }

    [Fact]
    public async Task AFacilityRegistrar_MayNotActForAnotherFacility()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.False(service.CanActForFacility(registrar!, OtherFacility));
    }

    [Theory]
    [InlineData(NcbrsRoles.DistrictOfficer)]
    [InlineData(NcbrsRoles.MinistryAdmin)]
    public async Task OversightRoles_MayActAcrossFacilities(string role)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: role);
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.True(service.CanActForFacility(registrar!, OtherFacility));
    }

    [Fact]
    public async Task ACommunityHealthWorker_IsStillConfinedToTheirFacility()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: NcbrsRoles.CommunityHealthWorker);
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.False(service.CanActForFacility(registrar!, OtherFacility));
    }
}

/// <summary>
/// Keycloak nests realm roles inside a JSON claim that ASP.NET's role
/// machinery cannot read. If this transformation breaks, every role check
/// silently matches nothing -- authorization failing open in the quiet
/// direction, which is why it is tested directly.
/// </summary>
public class KeycloakRoleClaimsTransformationTests
{
    private static ClaimsPrincipal PrincipalWith(string? realmAccessJson)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "subject") };

        if (realmAccessJson is not null)
        {
            claims.Add(new Claim(KeycloakRealmRoles.RealmAccessClaim, realmAccessJson));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    [Fact]
    public async Task RealmRoles_BecomeRoleClaims()
    {
        var principal = await new KeycloakRoleClaimsTransformation().TransformAsync(
            PrincipalWith("""{"roles":["facility-registrar","ministry-admin"]}"""));

        Assert.True(principal.IsInRole(NcbrsRoles.FacilityRegistrar));
        Assert.True(principal.IsInRole(NcbrsRoles.MinistryAdmin));
        Assert.False(principal.IsInRole(NcbrsRoles.DistrictOfficer));
    }

    [Fact]
    public async Task AMissingRealmAccessClaim_YieldsNoRoles()
    {
        var principal = await new KeycloakRoleClaimsTransformation().TransformAsync(PrincipalWith(null));

        Assert.False(principal.IsInRole(NcbrsRoles.FacilityRegistrar));
    }

    [Fact]
    public async Task AMalformedRealmAccessClaim_YieldsNoRoles_RatherThanThrowing()
    {
        var principal = await new KeycloakRoleClaimsTransformation().TransformAsync(
            PrincipalWith("not json at all"));

        Assert.False(principal.IsInRole(NcbrsRoles.FacilityRegistrar));
    }

    [Fact]
    public async Task RunningTwice_DoesNotDuplicateRoleClaims()
    {
        var transformation = new KeycloakRoleClaimsTransformation();

        var principal = await transformation.TransformAsync(
            PrincipalWith("""{"roles":["facility-registrar"]}"""));
        principal = await transformation.TransformAsync(principal);

        Assert.Single(principal.FindAll(ClaimTypes.Role));
    }
}
