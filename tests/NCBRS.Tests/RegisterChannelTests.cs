using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Online registration (<c>POST /api/birthrecords/register</c>) now holds a
/// device to the same proof a sync batch needs, and decides the channel from
/// the token rather than the body. Before this a stolen token could register
/// as any device, including a revoked one, and the audit trail recorded
/// whatever device id was typed.
/// </summary>
public class RegisterChannelTests : IDisposable
{
    private const string WebClient = "ncbrs-web";
    private const string DeviceClient = "ncbrs-device";

    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private int _nextBrn = 100_001;

    public RegisterChannelTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId,
            Name = "Juba Teaching Hospital",
            CountyCode = "SS0101",
            BrnBlockStart = 100_000,
            BrnBlockEnd = 199_999,
            BrnBlockNextAvailable = 100_500,
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
            Device("TABLET-1", DeviceStatus.Enrolled),
            Device("TABLET-REVOKED", DeviceStatus.Revoked));

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private static Device Device(string id, DeviceStatus status) => new()
    {
        DeviceId = id,
        FacilityId = FacilityId,
        PublicKeyPem = DeviceTestKeys.PublicKeyPem,
        Status = status,
        EnrolledAtUtc = DateTime.UtcNow.AddDays(-30),
        EnrolledByRegistrarId = RegistrarId,
    };

    private ApiRequest<RegisterBirthRequest> Birth(string deviceId) => new()
    {
        Data = new RegisterBirthRequest
        {
            Brn = (_nextBrn++).ToString(),
            FacilityId = FacilityId,
            ChildFullName = "Ayen Deng",
            DateOfBirth = DateTime.UtcNow.Date.AddDays(-3),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            BirthOrder = 1,
            DeviceId = deviceId,
        },
    };

    /// <summary>
    /// Posts the registration the way it arrives over HTTP: a token issued to
    /// <paramref name="client"/>, the body as bytes, and (optionally) a device
    /// signature over exactly those bytes.
    /// </summary>
    private async Task<(int Status, ActionResult<BirthRecordResponse> Result)> PostAsync(
        string client,
        ApiRequest<RegisterBirthRequest> envelope,
        Func<byte[], string>? sign = null,
        bool requireSignature = true)
    {
        await using var db = NewDb();

        var http = AuthTestContext.HttpContextFor(AuthTestContext.DefaultSubject, client, NcbrsRoles.FacilityRegistrar);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, WebJson);
        http.Request.Body = new MemoryStream(bytes) { Position = bytes.Length };
        if (sign is not null)
        {
            http.Request.Headers[DeviceSignature.HeaderName] = sign(bytes);
        }

        var current = AuthTestContext.RegistrarService(db, http);
        var districts = new CountyLookup(db);

        var controller = new BirthRecordsController(
            db,
            new BirthRegistrationService(
                db,
                new NoOpEventPublisher(),
                current,
                new DuplicateDetectionService(
                    db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
                    NullLogger<DuplicateDetectionService>.Instance, districts),
                districts,
                Options.Create(new StatutoryRegistrationOptions())),
            new AmendmentService(db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current, districts),
            current,
            districts,
            Options.Create(new StatutoryRegistrationOptions()),
            new DeviceEnrolmentService(db, new DeviceEnrolmentOptions { RequireSignature = requireSignature }),
            new RefusalAudit(db, NullLogger<RefusalAudit>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.Register(envelope);

        var status = result.Result switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => 200,
        };

        return (status, result);
    }

    private async Task<bool> RegisteredAsync(ApiRequest<RegisterBirthRequest> envelope)
    {
        await using var db = NewDb();
        return await db.BirthRecords.AnyAsync(record => record.Brn == envelope.Data.Brn);
    }

    private async Task<string?> RefusalFor(ApiRequest<RegisterBirthRequest> envelope)
    {
        await using var db = NewDb();
        return (await db.AuditLogs.FirstOrDefaultAsync(row =>
            row.EntityId == envelope.Data.Brn && row.Action.StartsWith("DeviceRefused:")))?.Action;
    }

    // --- the management site --------------------------------------------------------------

    [Fact]
    public async Task TheManagementSiteRegistersAsTheWebChannel()
    {
        var birth = Birth(WebClient);

        var (status, _) = await PostAsync(WebClient, birth);

        Assert.Equal(StatusCodes.Status201Created, status);
        Assert.True(await RegisteredAsync(birth));
    }

    /// <summary>A browser holds no device key, so a web session may not claim to be a tablet.</summary>
    [Fact]
    public async Task ABrowserSessionCannotRegisterAsADevice()
    {
        var birth = Birth("TABLET-1");

        var (status, _) = await PostAsync(WebClient, birth);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(await RegisteredAsync(birth));
        Assert.Equal("DeviceRefused:WrongChannel", await RefusalFor(birth));
    }

    // --- devices --------------------------------------------------------------------------

    [Fact]
    public async Task AnEnrolledDeviceWithItsSignatureRegisters()
    {
        var birth = Birth("TABLET-1");

        var (status, _) = await PostAsync(DeviceClient, birth, DeviceTestKeys.Sign);

        Assert.Equal(StatusCodes.Status201Created, status);
        Assert.True(await RegisteredAsync(birth));

        // Proving itself counts as being seen, exactly as on sync.
        await using var db = NewDb();
        Assert.NotNull((await db.Devices.SingleAsync(d => d.DeviceId == "TABLET-1")).LastSeenAtUtc);
    }

    /// <summary>The residual this closes: a stolen token, without the device's key.</summary>
    [Fact]
    public async Task AStolenTokenWithoutTheDevicesKeyIsRefused()
    {
        var birth = Birth("TABLET-1");

        var (status, _) = await PostAsync(DeviceClient, birth, sign: null);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(await RegisteredAsync(birth));
        Assert.Equal("DeviceRefused:SignatureFailed", await RefusalFor(birth));
    }

    [Fact]
    public async Task ASignatureFromAnotherKeyIsRefused()
    {
        var birth = Birth("TABLET-1");

        var (status, _) = await PostAsync(
            DeviceClient, birth, bytes => DeviceTestKeys.SignWithOtherKey(Encoding.UTF8.GetString(bytes)));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:SignatureFailed", await RefusalFor(birth));
    }

    /// <summary>
    /// Without this, the obvious bypass: a device token claims to be the
    /// management site to avoid proving which device it is.
    /// </summary>
    [Fact]
    public async Task ADeviceTokenCannotPoseAsTheManagementSite()
    {
        var birth = Birth(WebClient);

        var (status, _) = await PostAsync(DeviceClient, birth);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(await RegisteredAsync(birth));
        Assert.Equal("DeviceRefused:WrongChannel", await RefusalFor(birth));
    }

    [Fact]
    public async Task ARevokedDeviceIsRefusedEvenWithAValidSignature()
    {
        var birth = Birth("TABLET-REVOKED");

        var (status, _) = await PostAsync(DeviceClient, birth, DeviceTestKeys.Sign);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:NotActive", await RefusalFor(birth));
    }

    [Fact]
    public async Task AnUnenrolledDeviceIsRefused()
    {
        var birth = Birth("TABLET-NOBODY-ISSUED");

        var (status, _) = await PostAsync(DeviceClient, birth, DeviceTestKeys.Sign);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("DeviceRefused:NotEnrolled", await RefusalFor(birth));
    }

    /// <summary>
    /// The development switch behaves as it does for sync: with signatures not
    /// enforced an enrolled device registers unsigned — but enrolment and the
    /// channel rule still hold.
    /// </summary>
    [Fact]
    public async Task WithSignaturesNotEnforcedAnEnrolledDeviceRegistersUnsigned()
    {
        var birth = Birth("TABLET-1");

        var (status, _) = await PostAsync(DeviceClient, birth, sign: null, requireSignature: false);

        Assert.Equal(StatusCodes.Status201Created, status);
    }
}
