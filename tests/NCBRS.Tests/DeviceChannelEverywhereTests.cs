using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The device-channel rule on every write that names a device, not only
/// registration (plan §17, 9a): correction, BRN block grant, certificate
/// issue and reprint, the maternal questionnaire, and both outcomes.
///
/// Before this each of them wrote whatever <c>deviceId</c> the body carried
/// into the audit trail, so a stolen token could correct a child's name or
/// draw a facility's BRN range as any device it named -- including a revoked
/// one. Every endpoint runs the same cases, because the property does not
/// vary by endpoint: only the device can prove which device acted.
/// </summary>
public class DeviceChannelEverywhereTests : IDisposable
{
    private const string WebClient = AuthTestContext.WebClient;
    private const string DeviceClient = "ncbrs-device";
    private const string Brn = "100001";

    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid OtherFacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly DateTime BirthDate = DateTime.UtcNow.Date.AddDays(-5);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly CertificateSigner _signer;

    public DeviceChannelEverywhereTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        _signer = new CertificateSigner(
            Options.Create(new CertificateSigningOptions { AllowEphemeralDevelopmentKey = true }),
            new DevelopmentEnvironment(),
            NullLogger<CertificateSigner>.Instance);

        using var db = new NcbrsDbContext(_options);

        db.Facilities.AddRange(
            new Facility
            {
                FacilityId = FacilityId,
                Name = "Juba Teaching Hospital",
                CountyCode = "SS0101",
                BrnBlockStart = 100_000,
                BrnBlockEnd = 199_999,
                BrnBlockNextAvailable = 100_500,
            },
            new Facility
            {
                FacilityId = OtherFacilityId,
                Name = "Terekeka PHCC",
                CountyCode = "SS0102",
                BrnBlockStart = 200_000,
                BrnBlockEnd = 299_999,
                BrnBlockNextAvailable = 200_000,
            });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Lado",
            Role = RegistrarRole.FacilityRegistrar,
            CredentialHash = "test",
        });

        db.Devices.AddRange(
            Device("TABLET-1", FacilityId, DeviceStatus.Enrolled),
            Device("TABLET-REVOKED", FacilityId, DeviceStatus.Revoked),
            Device("TABLET-ELSEWHERE", OtherFacilityId, DeviceStatus.Enrolled));

        db.BirthRecords.Add(new BirthRecord
        {
            Brn = Brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Ayen Deng" },
            FacilityId = FacilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = BirthDate,
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            BirthOrder = 1,
            BirthWeightGrams = 3000,
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _signer.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private static Device Device(string id, Guid facilityId, DeviceStatus status) => new()
    {
        DeviceId = id,
        FacilityId = facilityId,
        PublicKeyPem = DeviceTestKeys.PublicKeyPem,
        Status = status,
        EnrolledAtUtc = DateTime.UtcNow.AddDays(-30),
        EnrolledByRegistrarId = RegistrarId,
    };

    // --- the endpoints ---------------------------------------------------------------------

    /// <summary>What a request needs, built fresh for each call.</summary>
    private sealed record Call(NcbrsDbContext Db, HttpContext Http, CurrentRegistrarService Current, DeviceChannelGate Gate);

    /// <summary>
    /// One gated endpoint: the body it takes for a given device id, and how
    /// to invoke it. Bodies are valid, so an accepted channel proceeds to
    /// the act rather than failing on something unrelated.
    /// </summary>
    private sealed record Endpoint(
        Func<string?, object> Body,
        Func<DeviceChannelEverywhereTests, Call, object, Task<IConvertToActionResult>> Invoke);

    private static readonly Dictionary<string, Endpoint> Endpoints = new()
    {
        ["amend"] = new(
            device => new ApiRequest<AmendBirthRecordRequest>
            {
                Data = new AmendBirthRecordRequest
                {
                    BirthWeightGrams = 3100, Reason = "Weight misread from the scale.", DeviceId = device ?? "",
                },
            },
            async (t, c, body) => await t.Records(c).Amend(Brn, (ApiRequest<AmendBirthRecordRequest>)body)),

        ["brn-block"] = new(
            device => new ApiRequest<BrnBlockRequest> { Data = new BrnBlockRequest { BlockSize = 10, DeviceId = device } },
            async (t, c, body) => await t.Records(c).RequestBrnBlock(FacilityId, (ApiRequest<BrnBlockRequest>)body)),

        ["certificate-issue"] = new(
            device => new ApiRequest<IssueCertificateRequest> { Data = new IssueCertificateRequest { DeviceId = device ?? "" } },
            async (t, c, body) => await t.Certificates(c).Issue(Brn, (ApiRequest<IssueCertificateRequest>)body)),

        ["certificate-reprint"] = new(
            device => new ApiRequest<IssueCertificateRequest> { Data = new IssueCertificateRequest { DeviceId = device ?? "" } },
            async (t, c, body) => await t.Certificates(c).Reprint(Brn, (ApiRequest<IssueCertificateRequest>)body)),

        ["maternal-statistics"] = new(
            device => new ApiRequest<CaptureMaternalStatisticsRequest>
            {
                Data = new CaptureMaternalStatisticsRequest { PrenatalVisitCount = 4, DeviceId = device ?? "" },
            },
            async (t, c, body) => await Statistics(c).Put(Brn, (ApiRequest<CaptureMaternalStatisticsRequest>)body)),

        ["neonatal-outcome"] = new(
            device => new ApiRequest<RecordNeonatalOutcomeRequest>
            {
                Data = new RecordNeonatalOutcomeRequest
                {
                    DeathDateUtc = BirthDate.AddDays(3), IcdPmCauseCode = "P21.9", DeviceId = device ?? "",
                },
            },
            async (t, c, body) => await Outcomes(c).RecordNeonatal(Brn, (ApiRequest<RecordNeonatalOutcomeRequest>)body)),

        ["maternal-outcome"] = new(
            device => new ApiRequest<RecordMaternalOutcomeRequest>
            {
                Data = new RecordMaternalOutcomeRequest
                {
                    DeathDateUtc = BirthDate.AddDays(2), IcdMmCauseCode = "O72.1", DeviceId = device ?? "",
                },
            },
            async (t, c, body) => await Outcomes(c).RecordMaternal(Brn, (ApiRequest<RecordMaternalOutcomeRequest>)body)),
    };

    public static TheoryData<string> AllEndpoints() => new(Endpoints.Keys);

    private BirthRecordsController Records(Call c)
    {
        var counties = new CountyLookup(c.Db);

        return new BirthRecordsController(
            c.Db,
            new BirthRegistrationService(
                c.Db, new NoOpEventPublisher(), c.Current,
                new DuplicateDetectionService(
                    c.Db, new DuplicateMatcher(), new CertificateRevocationRecorder(c.Db),
                    NullLogger<DuplicateDetectionService>.Instance, counties),
                counties,
                Options.Create(new StatutoryRegistrationOptions())),
            new AmendmentService(c.Db, new NoOpEventPublisher(), new CertificateRevocationRecorder(c.Db), c.Current, counties),
            c.Current,
            counties,
            Options.Create(new StatutoryRegistrationOptions()),
            c.Gate)
        {
            ControllerContext = new ControllerContext { HttpContext = c.Http },
        };
    }

    private CertificatesController Certificates(Call c)
        => new(new CertificateService(c.Db, _signer, c.Current, new CountyLookup(c.Db)), c.Current, c.Gate)
        {
            ControllerContext = new ControllerContext { HttpContext = c.Http },
        };

    private static MaternalStatisticsController Statistics(Call c)
        => new(new MaternalStatisticsService(c.Db, c.Current, new CountyLookup(c.Db)), c.Current, c.Gate)
        {
            ControllerContext = new ControllerContext { HttpContext = c.Http },
        };

    private static OutcomesController Outcomes(Call c)
        => new(new OutcomeService(c.Db, new NoOpEventPublisher(), c.Current, new CountyLookup(c.Db)), c.Current, c.Gate)
        {
            ControllerContext = new ControllerContext { HttpContext = c.Http },
        };

    /// <summary>
    /// Sends the request the way it arrives over HTTP: a token issued to
    /// <paramref name="client"/>, the body as bytes, and optionally a device
    /// signature over exactly those bytes. Returns the status code.
    /// </summary>
    private async Task<int> SendAsync(
        string endpoint,
        string client,
        string? deviceId,
        Func<byte[], string>? sign = null,
        DeviceEnrolmentOptions? enrolment = null)
    {
        var definition = Endpoints[endpoint];
        var body = definition.Body(deviceId);

        await using var db = NewDb();

        var http = AuthTestContext.HttpContextFor(AuthTestContext.DefaultSubject, client, NcbrsRoles.FacilityRegistrar);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), WebJson);
        http.Request.Body = new MemoryStream(bytes) { Position = bytes.Length };
        if (sign is not null)
        {
            http.Request.Headers[DeviceSignature.HeaderName] = sign(bytes);
        }

        var call = new Call(db, http, AuthTestContext.RegistrarService(db, http), AuthTestContext.ChannelGate(db, enrolment));
        var result = (await definition.Invoke(this, call, body)).Convert();

        return result switch
        {
            ObjectResult o => o.StatusCode ?? StatusCodes.Status200OK,
            StatusCodeResult s => s.StatusCode,
            _ => StatusCodes.Status200OK,
        };
    }

    private async Task<string?> RefusalAsync()
    {
        await using var db = NewDb();
        return (await db.AuditLogs.FirstOrDefaultAsync(row => row.Action.StartsWith("DeviceRefused:")))?.Action;
    }

    /// <summary>Every act these endpoints perform writes an audit row; a refusal writes only its own.</summary>
    private async Task<bool> AnythingActedAsync()
    {
        await using var db = NewDb();
        return await db.AuditLogs.AnyAsync(row => !row.Action.StartsWith("DeviceRefused:"));
    }

    // --- the rule, endpoint by endpoint ----------------------------------------------------

    /// <summary>The residual this closes: a stolen device token, without the device's key.</summary>
    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task AStolenTokenWithoutTheDevicesKeyIsRefused(string endpoint)
    {
        var status = await SendAsync(endpoint, DeviceClient, "TABLET-1", sign: null);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:SignatureFailed", await RefusalAsync());
        Assert.False(await AnythingActedAsync());
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task ABrowserSessionCannotActAsADevice(string endpoint)
    {
        var status = await SendAsync(endpoint, WebClient, "TABLET-1");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:WrongChannel", await RefusalAsync());
        Assert.False(await AnythingActedAsync());
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task ADeviceTokenCannotPoseAsTheManagementSite(string endpoint)
    {
        var status = await SendAsync(endpoint, DeviceClient, WebClient);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:WrongChannel", await RefusalAsync());
        Assert.False(await AnythingActedAsync());
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task ARevokedDeviceIsRefusedEvenWithAValidSignature(string endpoint)
    {
        var status = await SendAsync(endpoint, DeviceClient, "TABLET-REVOKED", DeviceTestKeys.Sign);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:NotActive", await RefusalAsync());
    }

    /// <summary>
    /// On a record, the facility the device must belong to is the record's --
    /// a body cannot name its way into another facility's register.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task ADeviceEnrolledElsewhereIsRefused(string endpoint)
    {
        var status = await SendAsync(endpoint, DeviceClient, "TABLET-ELSEWHERE", DeviceTestKeys.Sign);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:WrongFacility", await RefusalAsync());
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task TheManagementSiteActsAsTheWebChannel(string endpoint)
    {
        var status = await SendAsync(endpoint, WebClient, WebClient);

        Assert.NotEqual(StatusCodes.Status403Forbidden, status);
        Assert.Null(await RefusalAsync());
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task AnEnrolledDeviceWithItsSignatureActsAndIsSeen(string endpoint)
    {
        var status = await SendAsync(endpoint, DeviceClient, "TABLET-1", DeviceTestKeys.Sign);

        Assert.NotEqual(StatusCodes.Status403Forbidden, status);
        Assert.Null(await RefusalAsync());

        await using var db = NewDb();
        Assert.NotNull((await db.Devices.SingleAsync(d => d.DeviceId == "TABLET-1")).LastSeenAtUtc);
    }

    // --- the edges -------------------------------------------------------------------------

    /// <summary>
    /// A number that names no record answers the endpoint's own 404. A
    /// refusal would assert a record the register does not hold.
    /// </summary>
    [Fact]
    public async Task AnUnknownNumberIsNotFoundRatherThanRefused()
    {
        await using var db = NewDb();

        var http = AuthTestContext.HttpContextFor(AuthTestContext.DefaultSubject, DeviceClient, NcbrsRoles.FacilityRegistrar);
        var call = new Call(db, http, AuthTestContext.RegistrarService(db, http), AuthTestContext.ChannelGate(db));

        var result = await Records(call).Amend("999999", (ApiRequest<AmendBirthRecordRequest>)Endpoints["amend"].Body("TABLET-1"));

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Null(await RefusalAsync());
    }

    /// <summary>
    /// The device id is optional on a BRN block request, for the management
    /// site. A device must name itself: a grant nobody can attribute is a
    /// fortnight of registrations nobody can attribute either.
    /// </summary>
    [Fact]
    public async Task ADeviceMustNameItselfToDrawABlock()
    {
        var status = await SendAsync("brn-block", DeviceClient, deviceId: null, DeviceTestKeys.Sign);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:NotEnrolled", await RefusalAsync());
    }

    [Fact]
    public async Task TheManagementSiteMayDrawABlockWithoutNamingADevice()
    {
        var status = await SendAsync("brn-block", WebClient, deviceId: null);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(await RefusalAsync());
    }

    /// <summary>
    /// The development switch holds as on sync and registration: unsigned is
    /// allowed, but enrolment and the channel rule still apply.
    /// </summary>
    [Fact]
    public async Task WithSignaturesNotEnforcedTheChannelRuleStillHolds()
    {
        var relaxed = new DeviceEnrolmentOptions { RequireSignature = false };

        Assert.NotEqual(StatusCodes.Status403Forbidden,
            await SendAsync("amend", DeviceClient, "TABLET-1", enrolment: relaxed));
        Assert.Equal(StatusCodes.Status403Forbidden,
            await SendAsync("certificate-issue", WebClient, "TABLET-1", enrolment: relaxed));
    }
}
