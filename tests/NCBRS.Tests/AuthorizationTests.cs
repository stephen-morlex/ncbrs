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

    /// <summary>A second facility in the home facility's county (Terekeka).</summary>
    private static readonly Guid SameCountyFacility = Guid.Parse("0199a1b2-0003-7000-8000-000000000003");

    /// <summary>A facility the hierarchy cannot place in any county.</summary>
    private static readonly Guid UnplacedFacility = Guid.Parse("0199a1b2-0004-7000-8000-000000000004");

    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid UnplacedRegistrarId = Guid.Parse("0199a1b2-1002-7000-8000-000000000002");
    private const string UnplacedSubject = "22222222-2222-4222-8222-222222222222";

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
                Name = "Terekeka Village Health Post",
                CountyCode = "SS-CE-TER"
            },
            new Facility
            {
                FacilityId = OtherFacility,
                Name = "Juba Central Hospital",
                CountyCode = "SS-CE-JUB"
            },
            new Facility
            {
                FacilityId = SameCountyFacility,
                Name = "Tali Primary Health Care Unit",
                CountyCode = "SS-CE-TER"
            },
            new Facility
            {
                FacilityId = UnplacedFacility,
                Name = "Unplaced Health Post",
                CountyCode = AuditLog.Unknown
            });

        db.Registrars.AddRange(
            new Registrar
            {
                RegistrarId = RegistrarId,
                FacilityId = HomeFacility,
                ExternalSubjectId = AuthTestContext.DefaultSubject,
                DisplayName = "Nurse A. Lado",
                Role = RegistrarRole.FacilityRegistrar,
                CredentialHash = "test"
            },
            new Registrar
            {
                RegistrarId = UnplacedRegistrarId,
                FacilityId = UnplacedFacility,
                ExternalSubjectId = UnplacedSubject,
                DisplayName = "Officer at an unplaced facility",
                Role = RegistrarRole.DistrictOfficer,
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

        Assert.True(await service.CanActForFacilityAsync(registrar!, HomeFacility));
    }

    [Fact]
    public async Task AFacilityRegistrar_MayNotActForAnotherFacility()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.False(await service.CanActForFacilityAsync(registrar!, OtherFacility));
    }

    /// <summary>
    /// Even a facility in the same county: a registrar's reach is their own
    /// facility, not their county. County reach is an oversight role's.
    /// </summary>
    [Fact]
    public async Task AFacilityRegistrar_MayNotActForAnotherFacilityInTheirCounty()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.False(await service.CanActForFacilityAsync(registrar!, SameCountyFacility));
    }

    [Fact]
    public async Task AMinistryAdmin_ActsNationally()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: NcbrsRoles.MinistryAdmin);
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.True(await service.CanActForFacilityAsync(registrar!, OtherFacility));
        Assert.True(await service.CanActForFacilityAsync(registrar!, UnplacedFacility));
    }

    [Fact]
    public async Task ADistrictOfficer_MayActAcrossTheirOwnCounty()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: NcbrsRoles.DistrictOfficer);
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.True(await service.CanActForFacilityAsync(registrar!, SameCountyFacility));
    }

    /// <summary>
    /// The gap this closes. Reads were already confined to the officer's
    /// county, but writes were national, so a Terekeka district officer could
    /// approve an amendment, verify a late registration or enrol a device for
    /// a Juba record they were not permitted even to search for.
    /// </summary>
    [Fact]
    public async Task ADistrictOfficer_MayNotActInAnotherCounty()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: NcbrsRoles.DistrictOfficer);
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.False(await service.CanActForFacilityAsync(registrar!, OtherFacility));
    }

    /// <summary>
    /// Fails closed. Treating "unknown" as a county would let an officer reach
    /// every facility the hierarchy cannot place — the facilities least able
    /// to have their records double-checked.
    /// </summary>
    [Fact]
    public async Task ADistrictOfficer_MayNotActForAFacilityWithNoCounty()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: NcbrsRoles.DistrictOfficer);
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.False(await service.CanActForFacilityAsync(registrar!, UnplacedFacility));
    }

    /// <summary>
    /// And the other direction: an officer whose own facility has no county
    /// has no county to act across. "Unknown" matching "Unknown" would be the
    /// same hole from the other side.
    /// </summary>
    [Fact]
    public async Task ADistrictOfficer_WithNoCounty_IsConfinedToTheirOwnFacility()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(subject: UnplacedSubject, roles: NcbrsRoles.DistrictOfficer);
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.True(await service.CanActForFacilityAsync(registrar!, UnplacedFacility));
        Assert.False(await service.CanActForFacilityAsync(registrar!, HomeFacility));
        Assert.False(await service.CanActForFacilityAsync(registrar!, OtherFacility));
    }

    [Fact]
    public async Task ACommunityHealthWorker_IsStillConfinedToTheirFacility()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: NcbrsRoles.CommunityHealthWorker);
        var service = AuthTestContext.RegistrarService(db, http);

        var registrar = await service.GetAsync();

        Assert.False(await service.CanActForFacilityAsync(registrar!, OtherFacility));
        Assert.False(await service.CanActForFacilityAsync(registrar!, SameCountyFacility));
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
