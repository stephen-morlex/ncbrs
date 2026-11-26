using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Problems reported from tablets (plan §17 item 37). What these pin: a report
/// sent twice is stored once, and an id cannot be borrowed from another
/// device; the district sees its own county's reports and resolves only
/// those; and a report stays the size of a report.
/// </summary>
public class ProblemReportTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Terekeka = Guid.Parse("0199a1b2-0001-7000-8000-00000000e001");
    private static readonly Guid Juba = Guid.Parse("0199a1b2-0002-7000-8000-00000000e002");
    private static readonly Guid Nurse = Guid.Parse("0199a1b2-1001-7000-8000-00000000e001");

    private const string OfficerSubject = "officer-terekeka";
    private const string JubaOfficerSubject = "officer-juba";
    private const string MinistrySubject = "ministry";

    private readonly TestDatabase _database = TestDatabase.Create();

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    public ProblemReportTests()
    {
        using var db = NewDb();
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = Terekeka, Name = "Terekeka Village Health Post", CountyCode = "SS0104" },
            new Facility { FacilityId = Juba, Name = "Juba Teaching Hospital", CountyCode = "SS0101" });

        db.Registrars.AddRange(
            new Registrar { RegistrarId = Nurse, FacilityId = Terekeka, ExternalSubjectId = AuthTestContext.DefaultSubject, DisplayName = "Nurse Lado", CredentialHash = "x" },
            new Registrar { FacilityId = Terekeka, ExternalSubjectId = OfficerSubject, DisplayName = "Officer Kenyi", CredentialHash = "x", Role = RegistrarRole.DistrictOfficer },
            new Registrar { FacilityId = Juba, ExternalSubjectId = JubaOfficerSubject, DisplayName = "Officer Wani", CredentialHash = "x", Role = RegistrarRole.DistrictOfficer },
            new Registrar { FacilityId = Juba, ExternalSubjectId = MinistrySubject, DisplayName = "Ministry", CredentialHash = "x", Role = RegistrarRole.MinistryAdmin });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static ReportProblemRequest Request(Guid? id = null, string device = "TAB-1", Guid? facility = null) => new()
    {
        ReportId = id ?? Guid.CreateVersion7(),
        DeviceId = device,
        FacilityId = facility ?? Terekeka,
        WrittenAtUtc = Now.AddDays(-3),
        Description = "  Sync says the sign-in ended.  ",
        Diagnostics = new DeviceDiagnostics
        {
            AppVersion = "1.0.4",
            Platform = "Android 14",
            Language = "ar",
            SignedIn = false,
            StaffCount = 2,
            WaitingToSync = 7,
            Refused = 1,
            NumbersLeft = 140,
            ClockVerdict = "Behind",
            ClockOffsetSeconds = -86_400,
            LastSyncProblems = ["The centre did not accept the sign-in. Sign in again."],
        },
    };

    private static async Task<ProblemReportOutcome> StoreAsync(NcbrsDbContext db, ReportProblemRequest request)
    {
        var nurse = await db.Registrars.SingleAsync(registrar => registrar.RegistrarId == Nurse);
        return await new ProblemReportService(db, new CountyLookup(db), new FixedClock(Now)).StoreAsync(request, nurse, null);
    }

    [Fact]
    public async Task AReportIsStoredWithItsDiagnostics_AndAudited()
    {
        await using var db = NewDb();

        var outcome = await StoreAsync(db, Request());

        Assert.Equal(ProblemReportResult.Stored, outcome.Result);
        var report = outcome.Report!;
        Assert.Equal("Sync says the sign-in ended.", report.Description);
        Assert.Equal(("SS0104", "Terekeka Village Health Post", "Nurse Lado"), (report.CountyCode, report.FacilityName, report.ReportedByName));
        Assert.Equal((Now.AddDays(-3), Now), (report.WrittenAtUtc, report.ReceivedAtUtc));
        Assert.Equal(7, report.Diagnostics.WaitingToSync);
        Assert.Equal(["The centre did not accept the sign-in. Sign in again."], report.Diagnostics.LastSyncProblems);
        Assert.Equal("ProblemReported", (await db.AuditLogs.SingleAsync()).Action);
    }

    /// <summary>The tablet resends when a link drops after the registry stored it.</summary>
    [Fact]
    public async Task AReportSentTwiceIsStoredOnce()
    {
        await using var db = NewDb();
        var id = Guid.CreateVersion7();
        await StoreAsync(db, Request(id));

        var again = await StoreAsync(db, Request(id));

        Assert.Equal(ProblemReportResult.AlreadyStored, again.Result);
        Assert.Equal(1, await db.DeviceProblemReports.CountAsync());
    }

    [Fact]
    public async Task AnotherDeviceCannotBorrowAReportsId()
    {
        await using var db = NewDb();
        var id = Guid.CreateVersion7();
        await StoreAsync(db, Request(id));

        var borrowed = await StoreAsync(db, Request(id, device: "TAB-2"));

        Assert.Equal(ProblemReportResult.NotPermitted, borrowed.Result);
        Assert.Null(borrowed.Report);
    }

    [Fact]
    public void AReportStaysTheSizeOfAReport()
    {
        var validator = new ReportProblemRequestValidator();

        Assert.True(validator.Validate(Request()).IsValid);
        Assert.False(validator.Validate(Request() with { Description = new string('x', 1001) }).IsValid);
        Assert.False(validator.Validate(Request() with
        {
            Diagnostics = new DeviceDiagnostics { LastSyncProblems = [.. Enumerable.Repeat("x", 6)] },
        }).IsValid);
        Assert.False(validator.Validate(Request() with
        {
            Diagnostics = new DeviceDiagnostics { LastSyncProblems = [new string('x', 301)] },
        }).IsValid);
    }

    // --- the district's queue ------------------------------------------------------------------

    private ProblemReportsController Controller(NcbrsDbContext db, DefaultHttpContext http)
    {
        var current = AuthTestContext.RegistrarService(db, http);
        return new ProblemReportsController(
            new ProblemReportService(db, new CountyLookup(db), new FixedClock(Now)),
            current,
            new CountyScopeResolver(new CountyLookup(db)),
            AuthTestContext.ChannelGate(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static DefaultHttpContext As(string subject, string role) => AuthTestContext.HttpContextFor(subject, null, role);

    private static IReadOnlyList<ProblemReportResponse> Reports(ActionResult<IReadOnlyList<ProblemReportResponse>> result)
        => (IReadOnlyList<ProblemReportResponse>)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task AnOfficerSeesTheirCounty_AndTheMinistryEveryCounty()
    {
        await using var db = NewDb();
        await StoreAsync(db, Request());
        await StoreAsync(db, Request(device: "TAB-JUBA", facility: Juba));

        var county = Reports(await Controller(db, As(OfficerSubject, NcbrsRoles.DistrictOfficer)).List());
        var country = Reports(await Controller(db, As(MinistrySubject, NcbrsRoles.SystemAdmin)).List());

        Assert.Equal(["TAB-1"], county.Select(report => report.DeviceId));
        Assert.Equal(2, country.Count);
    }

    [Fact]
    public async Task AResolvedReportLeavesTheQueue_ButIsKept()
    {
        await using var db = NewDb();
        var stored = (await StoreAsync(db, Request())).Report!;
        var officer = Controller(db, As(OfficerSubject, NcbrsRoles.DistrictOfficer));

        var resolved = await officer.Resolve(stored.ReportId, new ApiRequest<ResolveProblemReportRequest>
        {
            Data = new ResolveProblemReportRequest { Note = "Signed the tablet in again by phone." },
        });

        Assert.Equal("Officer Kenyi", resolved.Value!.ResolvedByName);
        Assert.Empty(Reports(await officer.List()));
        Assert.Single(Reports(await officer.List(includeResolved: true)));
        Assert.Contains(await db.AuditLogs.Select(row => row.Action).ToListAsync(), action => action == "ProblemReportResolved");
    }

    [Fact]
    public async Task AReportIsResolvedOnce()
    {
        await using var db = NewDb();
        var stored = (await StoreAsync(db, Request())).Report!;
        var officer = Controller(db, As(OfficerSubject, NcbrsRoles.DistrictOfficer));
        var body = new ApiRequest<ResolveProblemReportRequest> { Data = new ResolveProblemReportRequest() };
        await officer.Resolve(stored.ReportId, body);

        var again = await officer.Resolve(stored.ReportId, body);

        Assert.Equal(StatusCodes.Status409Conflict, ((ObjectResult)again.Result!).StatusCode);
    }

    /// <summary>Resolving from another county would tell the report's own district it was dealt with.</summary>
    [Fact]
    public async Task AnotherCountysOfficerCannotResolveIt()
    {
        await using var db = NewDb();
        var stored = (await StoreAsync(db, Request())).Report!;

        var refused = await Controller(db, As(JubaOfficerSubject, NcbrsRoles.DistrictOfficer))
            .Resolve(stored.ReportId, new ApiRequest<ResolveProblemReportRequest> { Data = new ResolveProblemReportRequest() });

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)refused.Result!).StatusCode);
    }
}
