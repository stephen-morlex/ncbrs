using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Device enrolment (WS-B9).
///
/// Before this, <c>deviceId</c> was a string the caller asserted. Any account
/// with registration rights could upload an outbox under any device name, and
/// the audit trail recorded whatever was typed — against legal records.
///
/// Two properties are tested separately because they close different holes.
/// **Enrolment** says the id is one the Ministry issued to this facility.
/// **Possession** says the request actually came from that device, which is
/// what makes a stolen token insufficient on its own.
/// </summary>
public class DeviceEnrolmentTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid OtherFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");

    /// <summary>A second facility in the officer's own county (Terekeka).</summary>
    private static readonly Guid SameCountyFacilityId = Guid.Parse("0199a1b2-0003-7000-8000-000000000003");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public DeviceEnrolmentTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.AddRange(
            new Facility
            {
                FacilityId = FacilityId,
                Name = "Terekeka Village Health Post",
                CountyCode = "SS-CE-TER",
                BrnBlockStart = 100_000,
                BrnBlockEnd = 199_999,
                BrnBlockNextAvailable = 100_000
            },
            new Facility
            {
                FacilityId = OtherFacilityId,
                Name = "Juba Central Hospital",
                CountyCode = "SS-CE-JUB",
                BrnBlockStart = 200_000,
                BrnBlockEnd = 299_999,
                BrnBlockNextAvailable = 200_000
            },
            new Facility
            {
                FacilityId = SameCountyFacilityId,
                Name = "Tali Primary Health Care Unit",
                CountyCode = "SS-CE-TER",
                BrnBlockStart = 300_000,
                BrnBlockEnd = 399_999,
                BrnBlockNextAvailable = 300_000
            });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "D. Officer",
            Role = RegistrarRole.DistrictOfficer
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    // --- listing: the same county boundary on the read side ---------------------------------

    /// <summary>
    /// With no facility named, a district officer's list is their own county.
    /// It used to be every device in the country, to anyone who could enrol.
    /// </summary>
    [Fact]
    public async Task ADistrictOfficersDeviceListIsTheirOwnCounty()
    {
        await GivenEnrolledAsync("TABLET-TER-1", FacilityId);
        await GivenEnrolledAsync("TABLET-TER-2", SameCountyFacilityId);
        await GivenEnrolledAsync("TERMINAL-JUB-1", OtherFacilityId);

        await using var db = NewDb();
        var devices = (await Devices(db).List()).Value!;

        Assert.Equal(["TABLET-TER-1", "TABLET-TER-2"], devices.Select(d => d.DeviceId).Order().ToArray());
    }

    [Fact]
    public async Task TheMinistrysDeviceListIsTheWholeCountry()
    {
        await GivenEnrolledAsync("TABLET-TER-1", FacilityId);
        await GivenEnrolledAsync("TERMINAL-JUB-1", OtherFacilityId);

        await using var db = NewDb();
        var devices = (await Devices(db, NcbrsRoles.MinistryAdmin).List()).Value!;

        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public async Task NamingAFacilityInAnotherCountyIsRefused()
    {
        await GivenEnrolledAsync("TERMINAL-JUB-1", OtherFacilityId);

        await using var db = NewDb();
        var result = await Devices(db).List(OtherFacilityId);

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);
    }


    private static DevicesController Devices(NcbrsDbContext db, string role = NcbrsRoles.DistrictOfficer)
    {
        var http = AuthTestContext.HttpContextFor(roles: role);

        return new DevicesController(
            db, AuthTestContext.RegistrarService(db, http), new CountyLookup(db), new CountyScopeResolver(new CountyLookup(db)))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static ApiRequest<T> Envelope<T>(T data) => new() { Data = data };

    private static EnrolDeviceRequest Enrolment(
        string deviceId = "TABLET-07",
        Guid? facilityId = null,
        string? publicKeyPem = null)
        => new()
        {
            DeviceId = deviceId,
            FacilityId = facilityId ?? FacilityId,
            PublicKeyPem = publicKeyPem ?? DeviceTestKeys.PublicKeyPem,
            Label = "Terekeka post, tablet 2"
        };

    private async Task<Device> GivenEnrolledAsync(
        string deviceId = "TABLET-07",
        Guid? facilityId = null,
        DeviceStatus status = DeviceStatus.Enrolled,
        string? publicKeyPem = null)
    {
        await using var db = NewDb();

        var device = new Device
        {
            DeviceId = deviceId,
            FacilityId = facilityId ?? FacilityId,
            PublicKeyPem = publicKeyPem ?? DeviceTestKeys.PublicKeyPem,
            Status = status,
            EnrolledByRegistrarId = RegistrarId
        };

        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return device;
    }

    // CreatedAtActionResult is an ObjectResult, so this covers both the
    // success and error shapes; a bare value (no ActionResult) is a 200.
    private static int StatusOf(ActionResult<DeviceResponse> result) => result.Result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
        _ => StatusCodes.Status200OK
    };

    // --- the exit condition -----------------------------------------------------

    /// <summary>
    /// The plan's exit condition, stated directly: a valid user token from an
    /// unenrolled device is refused.
    /// </summary>
    [Fact]
    public async Task AValidTokenFromAnUnenrolledDevice_IsRefused()
    {
        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions())
            .CheckAsync("TABLET-UNKNOWN", FacilityId, ReadOnlyMemory<byte>.Empty, null);

        Assert.False(check.Accepted);
        Assert.Equal(DeviceCheckOutcome.NotEnrolled, check.Outcome);
        Assert.Contains("not enrolled", check.Detail);
    }

    /// <summary>
    /// And end to end through the sync endpoint, which is where it actually
    /// has to hold.
    /// </summary>
    [Fact]
    public async Task ABatchFromAnUnenrolledDevice_IsRefusedWithoutRegisteringAnything()
    {
        await using var db = NewDb();

        var result = await Sync(db).SubmitBatch(Envelope(new SyncBatchRequest
        {
            DeviceId = "TABLET-UNKNOWN",
            FacilityId = FacilityId,
            Records = [new SyncBirthRecord { Birth = Birth("100001") }]
        }));

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);

        await using var verify = NewDb();
        Assert.Empty(await verify.BirthRecords.ToListAsync());
        Assert.Empty(await verify.SyncBatches.ToListAsync());
    }

    /// <summary>
    /// A refused upload is the most interesting thing this endpoint sees.
    /// Discarding it would leave an attempt from a stolen token invisible.
    /// </summary>
    [Fact]
    public async Task ARefusedBatchIsStillAudited()
    {
        await using var db = NewDb();

        await Sync(db).SubmitBatch(Envelope(new SyncBatchRequest
        {
            DeviceId = "TABLET-UNKNOWN",
            FacilityId = FacilityId,
            Records = []
        }));

        await using var verify = NewDb();
        var audit = await verify.AuditLogs.SingleAsync(log => log.Action.StartsWith("DeviceRefused"));

        Assert.Equal("DeviceRefused:NotEnrolled", audit.Action);
        Assert.Equal("TABLET-UNKNOWN", audit.DeviceId);
    }

    // --- status -------------------------------------------------------------------

    [Theory]
    [InlineData(DeviceStatus.Suspended)]
    [InlineData(DeviceStatus.Revoked)]
    public async Task ADeviceThatIsNotEnrolled_MaySyncNothing(DeviceStatus status)
    {
        await GivenEnrolledAsync(status: status);

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions { RequireSignature = false })
            .CheckAsync("TABLET-07", FacilityId, ReadOnlyMemory<byte>.Empty, null);

        Assert.Equal(DeviceCheckOutcome.NotActive, check.Outcome);
    }

    /// <summary>
    /// Turning enforcement off is a migration aid, not an amnesty. A device
    /// someone revoked was a deliberate act, and a config flag that let
    /// stolen tablets back in would be a trap.
    /// </summary>
    [Fact]
    public async Task ARevokedDeviceIsRefusedEvenWhenEnrolmentIsNotEnforced()
    {
        await GivenEnrolledAsync(status: DeviceStatus.Revoked);

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(
                db, new DeviceEnrolmentOptions { Required = false, RequireSignature = false })
            .CheckAsync("TABLET-07", FacilityId, ReadOnlyMemory<byte>.Empty, null);

        Assert.Equal(DeviceCheckOutcome.NotActive, check.Outcome);
    }

    /// <summary>
    /// With enforcement off, an unknown device passes — that is the point of
    /// the flag — so the migration path is real rather than notional.
    /// </summary>
    [Fact]
    public async Task WithEnforcementOff_AnUnknownDeviceIsAllowedThrough()
    {
        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(
                db, new DeviceEnrolmentOptions { Required = false })
            .CheckAsync("TABLET-UNKNOWN", FacilityId, ReadOnlyMemory<byte>.Empty, null);

        Assert.True(check.Accepted);
    }

    /// <summary>
    /// A device is bound to the facility whose BRN block it draws from, so a
    /// batch for another facility carries numbers from a range that facility
    /// was never granted.
    /// </summary>
    [Fact]
    public async Task ADeviceEnrolledElsewhere_CannotSyncForThisFacility()
    {
        await GivenEnrolledAsync(facilityId: OtherFacilityId);

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions { RequireSignature = false })
            .CheckAsync("TABLET-07", FacilityId, ReadOnlyMemory<byte>.Empty, null);

        Assert.Equal(DeviceCheckOutcome.WrongFacility, check.Outcome);
    }

    // --- possession ------------------------------------------------------------------

    [Fact]
    public async Task ABatchSignedByTheEnrolledKeyIsAccepted()
    {
        await GivenEnrolledAsync();

        var body = """{"data":{"deviceId":"TABLET-07"}}""";

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions())
            .CheckAsync("TABLET-07", FacilityId, Encoding.UTF8.GetBytes(body), DeviceTestKeys.Sign(body));

        Assert.True(check.Accepted);
    }

    /// <summary>
    /// The case the whole mechanism exists for: an attacker holding a valid
    /// token and the right device id, but not the device's key.
    /// </summary>
    [Fact]
    public async Task ABatchSignedByADifferentKeyIsRefused()
    {
        await GivenEnrolledAsync();

        var body = """{"data":{"deviceId":"TABLET-07"}}""";

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions())
            .CheckAsync("TABLET-07", FacilityId, Encoding.UTF8.GetBytes(body),
                DeviceTestKeys.SignWithOtherKey(body));

        Assert.Equal(DeviceCheckOutcome.SignatureFailed, check.Outcome);
    }

    /// <summary>
    /// A signature is over the exact bytes, so altering one record in a
    /// batch invalidates it. This is what stops a district node — or anyone
    /// else on the path — editing a batch in transit.
    /// </summary>
    [Fact]
    public async Task AlteringTheBodyAfterSigningInvalidatesTheSignature()
    {
        await GivenEnrolledAsync();

        var signed = """{"data":{"brn":"100001"}}""";
        var altered = """{"data":{"brn":"100002"}}""";

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions())
            .CheckAsync("TABLET-07", FacilityId, Encoding.UTF8.GetBytes(altered),
                DeviceTestKeys.Sign(signed));

        Assert.Equal(DeviceCheckOutcome.SignatureFailed, check.Outcome);
    }

    [Fact]
    public async Task AMissingSignatureIsRefusedWhenSignaturesAreRequired()
    {
        await GivenEnrolledAsync();

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions())
            .CheckAsync("TABLET-07", FacilityId, Encoding.UTF8.GetBytes("{}"), null);

        Assert.Equal(DeviceCheckOutcome.SignatureFailed, check.Outcome);
        Assert.Contains(DeviceSignature.HeaderName, check.Detail);
    }

    [Fact]
    public async Task AMalformedSignatureHeaderIsRefusedRatherThanThrowing()
    {
        await GivenEnrolledAsync();

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions())
            .CheckAsync("TABLET-07", FacilityId, Encoding.UTF8.GetBytes("{}"), "not base64 at all!!");

        Assert.Equal(DeviceCheckOutcome.SignatureFailed, check.Outcome);
    }

    /// <summary>
    /// An unreadable enrolled key is reported as its own failure. Telling a
    /// field officer "signature invalid" would send them to replace a tablet
    /// that is working perfectly.
    /// </summary>
    [Fact]
    public async Task AnUnreadableEnrolledKeyIsReportedAsSuchRatherThanAsABadSignature()
    {
        await GivenEnrolledAsync(publicKeyPem: "-----BEGIN PUBLIC KEY-----\nnonsense\n-----END PUBLIC KEY-----");

        await using var db = NewDb();

        var check = await new DeviceEnrolmentService(db, new DeviceEnrolmentOptions())
            .CheckAsync("TABLET-07", FacilityId, Encoding.UTF8.GetBytes("{}"), DeviceTestKeys.Sign("{}"));

        Assert.Equal(DeviceCheckOutcome.SignatureFailed, check.Outcome);
        Assert.Contains("enrolled public key", check.Detail);
    }

    // --- enrolling -----------------------------------------------------------------------

    [Fact]
    public async Task ADeviceCanBeEnrolled()
    {
        await using var db = NewDb();

        var result = await Devices(db).Enrol(Envelope(Enrolment()));

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));

        await using var verify = NewDb();
        var device = await verify.Devices.SingleAsync();

        Assert.Equal("TABLET-07", device.DeviceId);
        Assert.Equal(DeviceStatus.Enrolled, device.Status);

        // Never reported yet, and that is a distinct state from "reported
        // long ago" -- it is how a failed deployment becomes visible.
        Assert.Null(device.LastSeenAtUtc);
    }

    /// <summary>
    /// Re-enrolment with a new key is the move that takes over an identity
    /// every existing record is attributed to. It is refused; replacing a
    /// device means revoking first, leaving both acts in the trail.
    /// </summary>
    [Fact]
    public async Task ReEnrollingAnExistingDeviceIsRefusedRatherThanReplacingItsKey()
    {
        await GivenEnrolledAsync();

        await using var db = NewDb();

        var result = await Devices(db).Enrol(Envelope(
            Enrolment(publicKeyPem: DeviceTestKeys.OtherPublicKeyPem)));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));

        await using var verify = NewDb();
        Assert.Equal(DeviceTestKeys.PublicKeyPem, (await verify.Devices.SingleAsync()).PublicKeyPem);
    }

    /// <summary>
    /// A device whose private key reached a server is a device whose
    /// signature proves nothing. Refused outright rather than trimmed to the
    /// public half, which would leave everyone believing otherwise.
    /// </summary>
    [Fact]
    public async Task EnrollingAPrivateKeyIsRefused()
    {
        await using var db = NewDb();

        var result = await Devices(db).Enrol(Envelope(
            Enrolment(publicKeyPem: DeviceTestKeys.PrivateKeyPem)));

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));

        await using var verify = NewDb();
        Assert.Empty(await verify.Devices.ToListAsync());
    }

    [Fact]
    public async Task AKeyOnTheWrongCurveIsRefusedAtEnrolmentRatherThanAtFirstSync()
    {
        await using var db = NewDb();

        var result = await Devices(db).Enrol(Envelope(
            Enrolment(publicKeyPem: DeviceTestKeys.WrongCurvePublicKeyPem)));

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    /// <summary>
    /// A district officer may enrol for any facility in their own county —
    /// enrolment uses the same authority model as every other oversight act,
    /// which is county-scoped for district officers and national only for
    /// ministry admins.
    /// </summary>
    [Fact]
    public async Task ADistrictOfficerMayEnrolForAFacilityInTheirCounty()
    {
        await using var db = NewDb();

        var result = await Devices(db).Enrol(Envelope(Enrolment(facilityId: SameCountyFacilityId)));

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));

        await using var verify = NewDb();
        Assert.Equal(SameCountyFacilityId, (await verify.Devices.SingleAsync()).FacilityId);
    }

    /// <summary>
    /// The residual this closes. It used to be allowed: an officer in Terekeka
    /// could put a device into service at a Juba hospital, and every record
    /// that device synced would be attributed to an enrolment nobody in Juba's
    /// county made. Refused now, and nothing is enrolled.
    /// </summary>
    [Fact]
    public async Task ADistrictOfficerMayNotEnrolInAnotherCounty()
    {
        await using var db = NewDb();

        var result = await Devices(db).Enrol(Envelope(Enrolment(facilityId: OtherFacilityId)));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));

        await using var verify = NewDb();
        Assert.False(await verify.Devices.AnyAsync());
    }

    [Fact]
    public async Task EnrollingAgainstANonExistentFacilityIsRefused()
    {
        await using var db = NewDb();

        var result = await Devices(db).Enrol(Envelope(Enrolment(facilityId: Guid.CreateVersion7())));

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    // --- suspension and revocation ---------------------------------------------------------

    [Fact]
    public async Task ASuspendedDeviceCanBeReinstated()
    {
        await GivenEnrolledAsync();

        await using var db = NewDb();
        var controller = Devices(db);

        Assert.Equal(200, StatusOf(await controller.Suspend(
            "TABLET-07", Envelope(new ChangeDeviceStatusRequest { Reason = "Mislaid at the post." }))));

        Assert.Equal(200, StatusOf(await controller.Reinstate(
            "TABLET-07", Envelope(new ChangeDeviceStatusRequest { Reason = "Found in a drawer." }))));

        await using var verify = NewDb();
        Assert.Equal(DeviceStatus.Enrolled, (await verify.Devices.SingleAsync()).Status);
    }

    /// <summary>
    /// There is no un-revoke. If a revocation was wrong the remedy is a
    /// fresh enrolment with a fresh key, which leaves both acts visible.
    /// </summary>
    [Fact]
    public async Task ARevokedDeviceCannotBeReinstated()
    {
        await GivenEnrolledAsync(status: DeviceStatus.Revoked);

        await using var db = NewDb();

        var result = await Devices(db).Reinstate(
            "TABLET-07", Envelope(new ChangeDeviceStatusRequest { Reason = "Changed my mind." }));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
    }

    /// <summary>
    /// A device barred with no explanation leaves the next officer unable to
    /// tell a stolen tablet from one withdrawn at end of life.
    /// </summary>
    [Fact]
    public async Task BarringADeviceRequiresAReason()
    {
        await GivenEnrolledAsync();

        await using var db = NewDb();

        var result = await Devices(db).Revoke(
            "TABLET-07", Envelope(new ChangeDeviceStatusRequest { Reason = "  " }));

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));

        await using var verify = NewDb();
        Assert.Equal(DeviceStatus.Enrolled, (await verify.Devices.SingleAsync()).Status);
    }

    [Fact]
    public async Task ChangingDeviceStatusIsAudited()
    {
        await GivenEnrolledAsync();

        await using var db = NewDb();

        await Devices(db).Revoke(
            "TABLET-07", Envelope(new ChangeDeviceStatusRequest { Reason = "Reported stolen." }));

        await using var verify = NewDb();
        var audit = await verify.AuditLogs.SingleAsync(log => log.Action == "RevokeDevice");

        Assert.Equal("TABLET-07", audit.EntityId);
        Assert.Equal("Reported stolen.", (await verify.Devices.SingleAsync()).StatusReason);
    }

    // --- last seen ------------------------------------------------------------------------

    /// <summary>
    /// A successful sync is what makes a device visibly alive — and its
    /// absence is what makes a post that never reported visible at all.
    /// </summary>
    [Fact]
    public async Task ASuccessfulSyncRecordsThatTheDeviceWasSeen()
    {
        await GivenEnrolledAsync();

        await using var db = NewDb();

        await Sync(db).SubmitBatch(Envelope(new SyncBatchRequest
        {
            DeviceId = "TABLET-07",
            FacilityId = FacilityId,
            Records = [new SyncBirthRecord { Birth = Birth("100001") }]
        }));

        await using var verify = NewDb();
        Assert.NotNull((await verify.Devices.SingleAsync()).LastSeenAtUtc);
    }

    // --- through the controller, over a real body ---------------------------------------------

    /// <summary>
    /// The signature is verified against the body read back from the request
    /// stream after model binding has already consumed it. That read is easy
    /// to get wrong in a way no service-level test would notice -- an
    /// un-rewound stream yields zero bytes, and zero bytes verify against
    /// nothing -- so it is exercised here through the controller with a real
    /// body.
    /// </summary>
    [Fact]
    public async Task ASignedBatchIsAcceptedThroughTheController()
    {
        await GivenEnrolledAsync();

        var body = JsonSerializer.Serialize(new
        {
            envelope = "v1",
            data = new
            {
                deviceId = "TABLET-07",
                facilityId = FacilityId,
                records = new[] { new { birth = Birth("100001") } }
            }
        });

        await using var db = NewDb();

        var result = await SignedSync(db, body, DeviceTestKeys.Sign(body))
            .SubmitBatch(Envelope(new SyncBatchRequest
            {
                DeviceId = "TABLET-07",
                FacilityId = FacilityId,
                Records = [new SyncBirthRecord { Birth = Birth("100001") }]
            }));

        Assert.Null(result.Result);
        Assert.Equal(1, result.Value!.Registered);
    }

    /// <summary>
    /// And the same path refuses a signature made over different bytes --
    /// proving the body really was read back, rather than the check passing
    /// on an empty buffer.
    /// </summary>
    [Fact]
    public async Task ABatchWithAMismatchedSignatureIsRefusedThroughTheController()
    {
        await GivenEnrolledAsync();

        await using var db = NewDb();

        var result = await SignedSync(db, """{"envelope":"v1","data":{}}""", DeviceTestKeys.Sign("something else"))
            .SubmitBatch(Envelope(new SyncBatchRequest
            {
                DeviceId = "TABLET-07",
                FacilityId = FacilityId,
                Records = [new SyncBirthRecord { Birth = Birth("100001") }]
            }));

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);

        await using var verify = NewDb();
        Assert.Empty(await verify.BirthRecords.ToListAsync());
    }

    private static SyncController SignedSync(NcbrsDbContext db, string body, string signature)
    {
        var controller = Sync(db, requireSignature: true);
        var http = controller.ControllerContext.HttpContext;

        // Seekable, and left at the end, exactly as model binding leaves it.
        var bytes = Encoding.UTF8.GetBytes(body);
        http.Request.Body = new MemoryStream(bytes) { Position = bytes.Length };
        http.Request.Headers[DeviceSignature.HeaderName] = signature;

        return controller;
    }

    // --- harness ----------------------------------------------------------------------------

    private static SyncController Sync(NcbrsDbContext db, bool requireSignature = false)
    {
        var http = AuthTestContext.HttpContextFor();
        var currentRegistrar = AuthTestContext.RegistrarService(db, http);
        var publisher = new NoOpEventPublisher();

        return new SyncController(
            db,
            new BirthRegistrationService(db, publisher, currentRegistrar,
                new DuplicateDetectionService(db, new DuplicateMatcher(),
                    new CertificateRevocationRecorder(db),
                    NullLogger<DuplicateDetectionService>.Instance,
                    new CountyLookup(db)),
                new CountyLookup(db),
                Options.Create(new StatutoryRegistrationOptions())),
            currentRegistrar,
            publisher,
            new ProvisionalRecordReconciler(db, new CountyLookup(db)),
            new DeviceEnrolmentService(db, new DeviceEnrolmentOptions { RequireSignature = requireSignature }),
            new RegisterBirthRequestValidator(),
            new CountyLookup(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static RegisterBirthRequest Birth(string brn) => new()
    {
        Brn = brn,
        FacilityId = FacilityId,
        ChildFullName = "Ayen Deng",
        DateOfBirth = DateTime.UtcNow.Date.AddDays(-2),
        Sex = Sex.Female,
        BirthWeightGrams = 3200,
        Plurality = BirthPlurality.Singleton,
        BirthOrder = 1,
        DeviceId = "TABLET-07"
    };
}
