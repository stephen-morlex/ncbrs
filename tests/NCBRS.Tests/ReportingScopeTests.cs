using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Models;
using NCBRS.Consumer.Services;
using NCBRS.Services;
using NCBRS.Web;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Reporting is scoped to the reader's county. The Ministry reads the whole
/// country or any one county; a district officer reads their own and nothing
/// else, as the web plan's role table always said. Before this, any district
/// officer could read the national dashboard, every county's unsuppressed
/// counts, other counties' silent devices and the national DHIS2 export.
///
/// The officer's county comes from the token's Keycloak group, because this
/// service cannot reach the registry (the API checks the two agree:
/// <c>CountyClaimConsistencyFilterTests</c>).
/// </summary>
public class ReportingScopeTests : IDisposable
{
    private const string Juba = "SS0101";
    private const string Terekeka = "SS0105";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ReadModelDbContext> _options;

    public ReportingScopeTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ReadModelDbContext>().UseSqlite(_connection).Options;

        using var db = new ReadModelDbContext(_options);
        db.Database.EnsureCreated();

        var born = DateTime.UtcNow.Date.AddDays(-40);
        db.AddRange(
            Fact("100001", Juba, born), Fact("100002", Juba, born), Fact("300001", Terekeka, born),
            Sync("TABLET-JUBA", Juba), Sync("TABLET-TEREKEKA", Terekeka));
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static RegistrationFact Fact(string brn, string county, DateTime born) => new()
    {
        Brn = brn, BirthRecordId = Guid.CreateVersion7(), CountyCode = county, FacilityId = Guid.NewGuid(),
        DateOfBirth = born, Sex = "Female", PublishedAtUtc = born.AddDays(1), RegisteredAtUtc = born.AddDays(1),
        FacilityTier = "Hospital", VitalEventType = "LiveBirth", WithinStatutoryWindow = true,
        ConfirmedAtUtc = born.AddDays(1),
    };

    private static SyncBatchFact Sync(string device, string county) => new()
    {
        SyncBatchId = Guid.CreateVersion7(), DeviceId = device, FacilityId = Guid.NewGuid(), CountyCode = county,
        Submitted = 1, Registered = 1, Status = "Reconciled", SyncedAtUtc = DateTime.UtcNow.AddDays(-60),
    };

    /// <summary>A caller as the bearer handler hands them over: roles flattened, groups as issued.</summary>
    private static ClaimsPrincipal Caller(string role, params string[] groups)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, role) };
        claims.AddRange(groups.Select(group => new Claim(KeycloakCounties.GroupsClaim, group)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static ClaimsPrincipal Ministry() => Caller(NcbrsRoles.MinistryAdmin);
    private static ClaimsPrincipal JubaOfficer() => Caller(NcbrsRoles.DistrictOfficer, $"/counties/{Juba}");

    private DashboardQueryService Dashboard(ReadModelDbContext db) => new(db, TimeProvider.System);

    private static int StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;

    private static T ValueOf<T>(IResult result) => (T)((IValueHttpResult)result).Value!;

    // --- reading the county from the token ----------------------------------------------------

    [Fact]
    public void TheCountyIsReadFromItsKeycloakGroup()
        => Assert.Equal([Juba], KeycloakCounties.Of(Caller(NcbrsRoles.DistrictOfficer, $"/counties/{Juba}", "/other/group")));

    /// <summary>
    /// ASP.NET's default inbound mapping renames <c>groups</c>. Both names are
    /// read, so switching that mapping cannot leave every officer countyless.
    /// </summary>
    [Fact]
    public void TheCountyIsReadUnderTheMappedClaimNameToo()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(KeycloakCounties.MappedGroupsClaim, $"/counties/{Juba}")], "Test"));

        Assert.Equal([Juba], KeycloakCounties.Of(user));
    }

    [Fact]
    public void AGroupNestedBelowACountyIsNotACounty()
        => Assert.Empty(KeycloakCounties.Of(Caller(NcbrsRoles.DistrictOfficer, $"/counties/{Juba}/clerks")));

    // --- the rule ---------------------------------------------------------------------------------

    [Fact]
    public void TheMinistryReadsTheWholeCountryOrAnyOneCounty()
    {
        Assert.Equal((true, (string?)null), (ReportingScope.Resolve(Ministry(), null).Allowed, ReportingScope.Resolve(Ministry(), null).CountyCode));
        Assert.Equal(Terekeka, ReportingScope.Resolve(Ministry(), Terekeka).CountyCode);
    }

    [Fact]
    public void AnOfficerReadsTheirOwnCountyByDefault()
    {
        var scope = ReportingScope.Resolve(JubaOfficer(), null);

        Assert.True(scope.Allowed);
        Assert.Equal(Juba, scope.CountyCode);
    }

    [Fact]
    public void AnOfficerAskingForAnotherCountyIsRefusedNotNarrowed()
        => Assert.False(ReportingScope.Resolve(JubaOfficer(), Terekeka).Allowed);

    /// <summary>Fails closed: no county, or two, is an account nobody placed.</summary>
    [Fact]
    public void AnOfficerTheIdentityProviderHasNotPlacedIsRefused()
    {
        Assert.False(ReportingScope.Resolve(Caller(NcbrsRoles.DistrictOfficer), null).Allowed);
        Assert.False(ReportingScope.Resolve(
            Caller(NcbrsRoles.DistrictOfficer, $"/counties/{Juba}", $"/counties/{Terekeka}"), null).Allowed);
    }

    // --- the endpoints apply it -------------------------------------------------------------------

    [Fact]
    public async Task AnOfficersSummaryIsTheirCountyNotTheCountry()
    {
        await using var db = new ReadModelDbContext(_options);

        var officer = ValueOf<DashboardSummary>(await ReportingEndpoints.SummaryAsync(
            null, null, null, JubaOfficer(), Dashboard(db), default));
        var national = ValueOf<DashboardSummary>(await ReportingEndpoints.SummaryAsync(
            null, null, null, Ministry(), Dashboard(db), default));

        Assert.Equal(Juba, officer.CountyCode);
        Assert.Equal(2, officer.Registrations.LiveBirths);
        Assert.Null(national.CountyCode);
        Assert.Equal(3, national.Registrations.LiveBirths);
    }

    [Fact]
    public async Task EveryScopedEndpointRefusesAnotherCounty()
    {
        await using var db = new ReadModelDbContext(_options);
        var officer = JubaOfficer();

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await ReportingEndpoints.SummaryAsync(
            null, null, Terekeka, officer, Dashboard(db), default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await ReportingEndpoints.TrendsAsync(
            null, null, Terekeka, officer, Dashboard(db), default)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await ReportingEndpoints.SilentDevicesAsync(
            7, Terekeka, officer, Dashboard(db), default)));
    }

    [Fact]
    public async Task TheCountyComparisonShowsAnOfficerOnlyTheirOwnRow()
    {
        await using var db = new ReadModelDbContext(_options);

        var officer = ValueOf<IReadOnlyList<CountySummary>>(await ReportingEndpoints.CountiesAsync(
            null, null, JubaOfficer(), Dashboard(db), default));
        var national = ValueOf<IReadOnlyList<CountySummary>>(await ReportingEndpoints.CountiesAsync(
            null, null, Ministry(), Dashboard(db), default));

        Assert.Equal([Juba], officer.Select(county => county.CountyCode));
        Assert.Equal(2, national.Count);
    }

    [Fact]
    public async Task SilentDevicesAreAnOfficersOwnCounty()
    {
        await using var db = new ReadModelDbContext(_options);

        var officer = ValueOf<IReadOnlyList<SilentDevice>>(await ReportingEndpoints.SilentDevicesAsync(
            7, null, JubaOfficer(), Dashboard(db), default));

        Assert.Equal(["TABLET-JUBA"], officer.Select(device => device.DeviceId));
    }

    [Fact]
    public async Task AnUnplacedOfficerGetsARefusalThatSaysWhy()
    {
        await using var db = new ReadModelDbContext(_options);

        var result = await ReportingEndpoints.SummaryAsync(
            null, null, null, Caller(NcbrsRoles.DistrictOfficer), Dashboard(db), default);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Contains("no county", ValueOf<ApiError>(result).Error);
    }
}
