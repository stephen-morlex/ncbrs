using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Web;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Onboarding and withdrawing registrars (pilot readiness §1). Before this,
/// only the Development seed could bind an account, so a pilot district's
/// staff would have been refused everywhere.
///
/// What these pin: an account declares itself from its own token; an officer
/// binds only within their county, only facility staff, and only a role the
/// account holds in Keycloak; and withdrawal takes effect everywhere at once
/// -- the account resolves to nobody, and its PIN leaves the tablets' bundle --
/// while the row, which the trail names, is kept.
/// </summary>
public class RegistrarOnboardingTests : IDisposable
{
    private static readonly Guid JubaPost = Guid.CreateVersion7();
    private static readonly Guid JubaHospital = Guid.CreateVersion7();
    private static readonly Guid TerekekaPost = Guid.CreateVersion7();

    private const string OfficerSubject = "officer-juba";
    private const string MinistrySubject = "ministry";
    private const string NewNurse = "new-nurse";

    private static readonly Guid OfficerId = Guid.CreateVersion7();
    private static readonly Guid MinistryId = Guid.CreateVersion7();

    private readonly TestDatabase _database = TestDatabase.Create();

    public RegistrarOnboardingTests()
    {
        using var db = NewDb();
        db.Facilities.AddRange(
            new Facility { FacilityId = JubaPost, Name = "Rejaf PHCU", CountyCode = "SS0101", BrnBlockStart = 100_000, BrnBlockEnd = 199_999 },
            new Facility { FacilityId = JubaHospital, Name = "Juba Teaching Hospital", CountyCode = "SS0101", BrnBlockStart = 200_000, BrnBlockEnd = 299_999 },
            new Facility { FacilityId = TerekekaPost, Name = "Tali PHCU", CountyCode = "SS0105", BrnBlockStart = 300_000, BrnBlockEnd = 399_999 });
        db.Registrars.AddRange(
            new Registrar { RegistrarId = OfficerId, FacilityId = JubaHospital, ExternalSubjectId = OfficerSubject, DisplayName = "Officer Kenyi", Role = RegistrarRole.DistrictOfficer },
            new Registrar { RegistrarId = MinistryId, FacilityId = JubaHospital, ExternalSubjectId = MinistrySubject, DisplayName = "Ministry Admin", Role = RegistrarRole.MinistryAdmin });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static ClaimsPrincipal Account(string subject, string[] roles, string[]? groups = null, string name = "Achol Garang")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject),
            new("name", name),
            new("preferred_username", "achol.garang"),
            new("email", "achol@health.gov.ss"),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange((groups ?? []).Select(group => new Claim(KeycloakCounties.GroupsClaim, group)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static ClaimsPrincipal Officer => Account(OfficerSubject, [NcbrsRoles.DistrictOfficer], ["/counties/SS0101"], "Officer Kenyi");

    private static ClaimsPrincipal Ministry => Account(MinistrySubject, [NcbrsRoles.MinistryAdmin], name: "Ministry Admin");

    private static (RegistrarOnboardingService Service, CurrentRegistrarService Current) For(NcbrsDbContext db, ClaimsPrincipal user)
    {
        var current = new CurrentRegistrarService(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } }, new CountyLookup(db));
        return (new RegistrarOnboardingService(db, current, new CountyLookup(db)), current);
    }

    private async Task DeclareAsync(string subject, string[] roles, string[]? groups = null)
    {
        await using var db = NewDb();
        var account = Account(subject, roles, groups);
        await For(db, account).Service.RecordAsync(account);
    }

    private async Task<RegistrarOnboardingOutcome> BindAsync(ClaimsPrincipal actor, string subject, Guid facility, RegistrarRole role)
    {
        await using var db = NewDb();
        var (service, current) = For(db, actor);
        var caller = (await current.GetAsync())!;
        var pendingId = (await db.PendingAccounts.FirstOrDefaultAsync(account => account.Subject == subject))?.PendingAccountId
                        ?? Guid.CreateVersion7();
        return await service.BindAsync(new BindRegistrarRequest { PendingAccountId = pendingId, FacilityId = facility, Role = role }, caller, actor, null);
    }

    // --- declaring ---------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownAccountDeclaresItselfFromItsOwnToken()
    {
        await DeclareAsync(NewNurse, [NcbrsRoles.FacilityRegistrar, "offline_access"], ["/counties/SS0101"]);
        await DeclareAsync(NewNurse, [NcbrsRoles.FacilityRegistrar, "offline_access"], ["/counties/SS0101"]);

        await using var db = NewDb();
        var pending = await db.PendingAccounts.SingleAsync();
        Assert.Equal(NewNurse, pending.Subject);
        Assert.Equal("Achol Garang", pending.DisplayName);
        Assert.Equal("achol.garang", pending.Username);
        Assert.Equal("achol@health.gov.ss", pending.Email);
        Assert.Equal(NcbrsRoles.FacilityRegistrar, pending.RealmRoles); // only the registry's roles
        Assert.Equal("SS0101", pending.CountyCode);
    }

    [Fact]
    public async Task ABoundAccountIsNotPending()
    {
        await using var db = NewDb();
        var status = await For(db, Officer).Service.RecordAsync(Officer);

        Assert.Equal(OfficerId, status.Registrar!.RegistrarId);
        Assert.Null(status.Pending);
        Assert.False(await db.PendingAccounts.AnyAsync());
    }

    [Fact]
    public async Task AnOfficerSeesOnlyAccountsInTheirCounty_TheMinistryAll()
    {
        await DeclareAsync("juba-nurse", [NcbrsRoles.FacilityRegistrar], ["/counties/SS0101"]);
        await DeclareAsync("terekeka-nurse", [NcbrsRoles.FacilityRegistrar], ["/counties/SS0105"]);
        await DeclareAsync("no-county", [NcbrsRoles.FacilityRegistrar]);

        await using var db = NewDb();
        var officer = await For(db, Officer).Current.GetAsync();
        var officerSees = await For(db, Officer).Service.PendingForAsync(officer!, Officer);
        var ministry = await For(db, Ministry).Current.GetAsync();
        var ministrySees = await For(db, Ministry).Service.PendingForAsync(ministry!, Ministry);

        Assert.Equal(["juba-nurse"], officerSees!.Select(account => account.Subject));
        Assert.Equal(3, ministrySees!.Count);
    }

    // --- binding -----------------------------------------------------------------------

    [Fact]
    public async Task AnOfficerBindsANurseInTheirCounty_AndTheAccountWorksAtOnce()
    {
        await DeclareAsync(NewNurse, [NcbrsRoles.FacilityRegistrar], ["/counties/SS0101"]);

        var outcome = await BindAsync(Officer, NewNurse, JubaPost, RegistrarRole.FacilityRegistrar);

        Assert.Equal(RegistrarOnboardingResult.Done, outcome.Result);
        await using var db = NewDb();
        Assert.False(await db.PendingAccounts.AnyAsync());
        var nurse = await For(db, Account(NewNurse, [NcbrsRoles.FacilityRegistrar])).Current.GetAsync();
        Assert.Equal(JubaPost, nurse!.FacilityId);
        Assert.Equal("Achol Garang", nurse.DisplayName);
        Assert.Contains(await db.AuditLogs.ToListAsync(), row => row.Action == "RegistrarBound:FacilityRegistrar" && row.UserId == OfficerId);
    }

    [Fact]
    public async Task AnOfficerCannotBindIntoAnotherCounty()
    {
        await DeclareAsync(NewNurse, [NcbrsRoles.FacilityRegistrar]);

        Assert.Equal(RegistrarOnboardingResult.NotPermitted, (await BindAsync(Officer, NewNurse, TerekekaPost, RegistrarRole.FacilityRegistrar)).Result);
    }

    /// <summary>An officer able to create officers could widen their own oversight.</summary>
    [Fact]
    public async Task AnOfficerBindsFacilityStaffOnly()
    {
        await DeclareAsync(NewNurse, [NcbrsRoles.DistrictOfficer], ["/counties/SS0101"]);

        Assert.Equal(RegistrarOnboardingResult.NotPermitted, (await BindAsync(Officer, NewNurse, JubaPost, RegistrarRole.DistrictOfficer)).Result);
        Assert.Equal(RegistrarOnboardingResult.Done, (await BindAsync(Ministry, NewNurse, JubaPost, RegistrarRole.DistrictOfficer)).Result);
    }

    /// <summary>The registry records who someone is; it does not grant what Keycloak has not.</summary>
    [Fact]
    public async Task ARoleTheAccountDoesNotHoldInKeycloakIsRefused()
    {
        await DeclareAsync(NewNurse, [NcbrsRoles.CommunityHealthWorker], ["/counties/SS0101"]);

        var outcome = await BindAsync(Officer, NewNurse, JubaPost, RegistrarRole.FacilityRegistrar);

        Assert.Equal(RegistrarOnboardingResult.Refused, outcome.Result);
        Assert.Contains("facility-registrar", outcome.Detail);
    }

    [Fact]
    public async Task AnAccountPlacedInAnotherCountyIsNotBoundHere()
    {
        await DeclareAsync(NewNurse, [NcbrsRoles.FacilityRegistrar], ["/counties/SS0105"]);

        Assert.Equal(RegistrarOnboardingResult.Refused, (await BindAsync(Ministry, NewNurse, JubaPost, RegistrarRole.FacilityRegistrar)).Result);
    }

    /// <summary>Bound without a county group, an officer would be refused every dashboard.</summary>
    [Fact]
    public async Task AnOfficerWithoutACountyGroupIsNotBound()
    {
        await DeclareAsync(NewNurse, [NcbrsRoles.DistrictOfficer]);

        Assert.Equal(RegistrarOnboardingResult.Refused, (await BindAsync(Ministry, NewNurse, JubaPost, RegistrarRole.DistrictOfficer)).Result);
    }

    [Fact]
    public async Task OnlyAnAccountThatHasDeclaredItselfCanBeBound()
    {
        Assert.Equal(RegistrarOnboardingResult.NotFound, (await BindAsync(Officer, "never-signed-in", JubaPost, RegistrarRole.FacilityRegistrar)).Result);
    }

    // --- withdrawing ---------------------------------------------------------------------

    private async Task<Guid> BoundNurseWithPinAsync()
    {
        await DeclareAsync(NewNurse, [NcbrsRoles.FacilityRegistrar], ["/counties/SS0101"]);
        var bound = await BindAsync(Officer, NewNurse, JubaPost, RegistrarRole.FacilityRegistrar);

        await using var db = NewDb();
        var nurse = await db.Registrars.SingleAsync(entry => entry.RegistrarId == bound.Registrar!.RegistrarId);
        nurse.CredentialHash = new DevicePinHasher().Hash("246813", iterations: 1_000);
        await db.SaveChangesAsync();
        return nurse.RegistrarId;
    }

    private async Task<IReadOnlyList<Guid>> PinBundleAsync()
    {
        await using var db = NewDb();
        var http = new DefaultHttpContext { User = Officer };
        var controller = new DeviceCredentialsController(db, new DevicePinHasher(), For(db, Officer).Current, new CountyLookup(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var bundle = (await controller.GetCredentials(JubaPost)).Value!;
        return bundle.Credentials.Select(entry => entry.RegistrarId).ToList();
    }

    [Fact]
    public async Task AWithdrawnRegistrarResolvesToNobody_AndTheirPinLeavesTheTablets()
    {
        var nurseId = await BoundNurseWithPinAsync();
        Assert.Contains(nurseId, await PinBundleAsync());

        await using (var db = NewDb())
        {
            var (service, current) = For(db, Officer);
            var outcome = await service.WithdrawAsync(nurseId, new WithdrawRegistrarRequest { Reason = "Moved to Torit." }, (await current.GetAsync())!, Officer, null);
            Assert.Equal(RegistrarOnboardingResult.Done, outcome.Result);
        }

        Assert.DoesNotContain(nurseId, await PinBundleAsync());

        await using var check = NewDb();
        Assert.Null(await For(check, Account(NewNurse, [NcbrsRoles.FacilityRegistrar])).Current.GetAsync());

        // Kept, not deleted: the trail names them.
        var row = await check.Registrars.SingleAsync(entry => entry.RegistrarId == nurseId);
        Assert.Equal("Moved to Torit.", row.WithdrawnReason);
        Assert.Equal(OfficerId, row.WithdrawnByRegistrarId);
        Assert.Contains(await check.AuditLogs.ToListAsync(), audit => audit.Action == "RegistrarWithdrawn" && audit.EntityId == nurseId.ToString());

        // And a withdrawn account does not slip back into the queue.
        var status = await For(check, Account(NewNurse, [NcbrsRoles.FacilityRegistrar])).Service.RecordAsync(Account(NewNurse, [NcbrsRoles.FacilityRegistrar]));
        Assert.True(status.Withdrawn);
        Assert.False(await check.PendingAccounts.AnyAsync());
    }

    [Fact]
    public async Task NobodyWithdrawsThemselves_AndAnOfficerDoesNotWithdrawAnOfficer()
    {
        await using var db = NewDb();
        var (service, current) = For(db, Officer);
        var officer = (await current.GetAsync())!;

        Assert.Equal(RegistrarOnboardingResult.Refused,
            (await service.WithdrawAsync(OfficerId, new WithdrawRegistrarRequest { Reason = "x" }, officer, Officer, null)).Result);
        Assert.Equal(RegistrarOnboardingResult.NotPermitted,
            (await service.WithdrawAsync(MinistryId, new WithdrawRegistrarRequest { Reason = "x" }, officer, Officer, null)).Result);
    }

    /// <summary>
    /// The subject identifies an account to Keycloak and is never published,
    /// as the directory holds; a waiting account is bound by the registry's own id.
    /// </summary>
    [Fact]
    public void AWaitingAccountIsNeverPublishedByItsKeycloakSubject()
    {
        var published = typeof(PendingAccountResponse).GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain("Subject", published);
        Assert.Contains("PendingAccountId", published);
        Assert.DoesNotContain("Subject", typeof(BindRegistrarRequest).GetProperties().Select(property => property.Name));
    }

    [Theory]
    [InlineData(nameof(RegistrarsController.Pending))]
    [InlineData(nameof(RegistrarsController.Bind))]
    [InlineData(nameof(RegistrarsController.Withdraw))]
    public void OnboardingIsAnOversightAct(string action)
    {
        var method = typeof(RegistrarsController).GetMethod(action)!;
        Assert.Equal(NcbrsRoles.CanManageRegistrars, method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }
}
