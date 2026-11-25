using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Web;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Binding staff from a CSV file (plan §17 item 38). What these pin: a check
/// binds nobody; a file binds everyone or nobody; and a file grants nothing a
/// single binding would not: the officer's own county, facility staff only, a
/// role held in Keycloak.
/// </summary>
public class RegistrarImportTests : IDisposable
{
    private static readonly Guid Rejaf = Guid.CreateVersion7();
    private static readonly Guid JubaHospital = Guid.CreateVersion7();
    private static readonly Guid Tali = Guid.CreateVersion7();
    private static readonly Guid TaliTwin = Guid.CreateVersion7();

    private const string OfficerSubject = "officer-juba";
    private const string MinistrySubject = "ministry";

    private readonly TestDatabase _database = TestDatabase.Create();

    public RegistrarImportTests()
    {
        using var db = NewDb();
        db.Facilities.AddRange(
            new Facility { FacilityId = Rejaf, Name = "Rejaf PHCU", CountyCode = "SS0101", OfficeCode = "RPH", BrnBlockStart = 100_000, BrnBlockEnd = 199_999 },
            new Facility { FacilityId = JubaHospital, Name = "Juba Teaching Hospital", CountyCode = "SS0101", BrnBlockStart = 200_000, BrnBlockEnd = 299_999 },
            new Facility { FacilityId = Tali, Name = "Tali PHCU", CountyCode = "SS0105", BrnBlockStart = 300_000, BrnBlockEnd = 399_999 },
            // A second facility with Tali's name, in another county: only the Ministry sees both.
            new Facility { FacilityId = TaliTwin, Name = "Tali PHCU", CountyCode = "SS0101", BrnBlockStart = 400_000, BrnBlockEnd = 499_999 });
        db.Registrars.AddRange(
            new Registrar { FacilityId = JubaHospital, ExternalSubjectId = OfficerSubject, DisplayName = "Officer Kenyi", Role = RegistrarRole.DistrictOfficer },
            new Registrar { FacilityId = JubaHospital, ExternalSubjectId = MinistrySubject, DisplayName = "Ministry Admin", Role = RegistrarRole.MinistryAdmin });
        db.PendingAccounts.AddRange(
            Waiting("achol.garang", "Achol Garang", NcbrsRoles.FacilityRegistrar, "SS0101"),
            Waiting("deng.majok", "Deng Majok", NcbrsRoles.CommunityHealthWorker, "SS0101"),
            Waiting("wani.lado", "Wani Lado", NcbrsRoles.FacilityRegistrar, "SS0105"),
            Waiting("kuol.ajak", "Kuol Ajak", NcbrsRoles.DistrictOfficer, "SS0101"));
        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static PendingAccount Waiting(string username, string name, string role, string county) => new()
    {
        Subject = $"sub-{username}",
        DisplayName = name,
        Username = username,
        RealmRoles = $"{role},offline_access",
        CountyCode = county,
    };

    private static ClaimsPrincipal As(string subject, string role)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Role, role)], "Test", ClaimTypes.Name, ClaimTypes.Role));

    private static readonly ClaimsPrincipal Officer = As(OfficerSubject, NcbrsRoles.DistrictOfficer);
    private static readonly ClaimsPrincipal Ministry = As(MinistrySubject, NcbrsRoles.SystemAdmin);

    private async Task<ImportRegistrarsResponse> ImportAsync(ClaimsPrincipal user, string csv, bool apply)
    {
        await using var db = NewDb();
        var current = new CurrentRegistrarService(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } }, new CountyLookup(db));
        var caller = (await current.GetAsync())!;
        var onboarding = new RegistrarOnboardingService(db, current, new CountyLookup(db));
        return await new RegistrarImportService(db, onboarding, current)
            .ImportAsync(new ImportRegistrarsRequest { Csv = csv, Apply = apply }, caller, user, null);
    }

    private const string Clean = """
        username,facility,role,display_name
        achol.garang,rph,Facility registrar,
        Deng.Majok,Juba Teaching Hospital,community-health-worker,Deng Majok Garang
        """;

    [Fact]
    public async Task ACheckBindsNobody()
    {
        var result = await ImportAsync(Officer, Clean, apply: false);

        Assert.False(result.Applied);
        Assert.Equal((2, 0), (result.Valid, result.Invalid));
        Assert.Equal(["Rejaf PHCU", "Juba Teaching Hospital"], result.Rows.Select(row => row.FacilityName));
        Assert.Equal(["Achol Garang", "Deng Majok Garang"], result.Rows.Select(row => row.DisplayName));

        await using var db = NewDb();
        Assert.Equal(4, await db.PendingAccounts.CountAsync());
    }

    [Fact]
    public async Task ApplyingBindsEveryoneAsIfBoundByHand()
    {
        var result = await ImportAsync(Officer, Clean, apply: true);

        Assert.True(result.Applied);
        await using var db = NewDb();
        var bound = await db.Registrars.Where(registrar => registrar.ExternalSubjectId!.StartsWith("sub-"))
            .OrderBy(registrar => registrar.DisplayName).ToListAsync();
        Assert.Equal(
            [("Achol Garang", Rejaf, RegistrarRole.FacilityRegistrar), ("Deng Majok Garang", JubaHospital, RegistrarRole.CommunityHealthWorker)],
            bound.Select(registrar => (registrar.DisplayName, registrar.FacilityId, registrar.Role)));
        Assert.Equal(2, await db.PendingAccounts.CountAsync());

        var audit = await db.AuditLogs.Select(row => row.Action).ToListAsync();
        Assert.Equal(2, audit.Count(action => action.StartsWith("RegistrarBound:", StringComparison.Ordinal)));
        Assert.Contains("RegistrarsImported:2", audit);
    }

    [Fact]
    public async Task OneBadRowAndNobodyIsBound()
    {
        var result = await ImportAsync(Officer, Clean + "\nnobody.here,RPH,FacilityRegistrar,", apply: true);

        Assert.False(result.Applied);
        Assert.Contains("No account is waiting for 'nobody.here'", result.Rows[2].Problems.Single());
        await using var db = NewDb();
        Assert.Equal(4, await db.PendingAccounts.CountAsync());
    }

    /// <summary>A file grants nothing a single binding would not.</summary>
    [Fact]
    public async Task EveryRowIsHeldToTheLimitsOnBindingOne()
    {
        var result = await ImportAsync(Officer, """
            username,facility,role
            wani.lado,Tali PHCU,FacilityRegistrar
            kuol.ajak,RPH,DistrictOfficer
            deng.majok,RPH,FacilityRegistrar
            deng.majok,Nowhere Clinic,CommunityHealthWorker
            achol.garang,RPH,FacilityRegistrar
            achol.garang,RPH,FacilityRegistrar
            ,RPH,Supervisor
            """, apply: false);

        var problems = result.Rows.ToDictionary(row => row.Line, row => string.Join(" ", row.Problems));
        // The officer's county holds a facility called Tali PHCU too: they are
        // told it is the wrong county by the binding itself, from Keycloak's group.
        Assert.Contains("county", problems[2]);
        Assert.Contains("Other roles are the Ministry's to give", problems[3]);
        Assert.Contains("does not hold the facility-registrar role", problems[4]);
        Assert.Contains("No facility has the office code or name 'Nowhere Clinic'", problems[5]);
        Assert.Empty(result.Rows.Single(row => row.Line == 6).Problems);
        Assert.Contains("Line 6 already binds 'achol.garang'", problems[7]);
        Assert.Contains("The role must be one of", problems[8]);
        Assert.Contains("username", problems[8]);
    }

    /// <summary>A name can recur across counties; one the Ministry cannot tell apart asks for the office code.</summary>
    [Fact]
    public async Task ANameThatMatchesTwoFacilitiesAsksForTheOfficeCode()
    {
        var result = await ImportAsync(Ministry, "username,facility,role\nwani.lado,Tali PHCU,FacilityRegistrar", apply: false);

        Assert.Contains("2 facilities are called 'Tali PHCU'", result.Rows.Single().Problems.Single());
    }

    [Fact]
    public async Task AFileThatIsNotAListOfPeopleIsSaidToBe()
    {
        var result = await ImportAsync(Officer, "username,role\nachol.garang,FacilityRegistrar", apply: true);

        Assert.Contains("Missing: facility", result.Rows.Single().Problems.Single());
    }
}
