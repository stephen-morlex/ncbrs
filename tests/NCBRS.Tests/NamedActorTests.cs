using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Two acts the register recorded but would not say who performed.
///
/// Both ids were stored from the start and neither was published, so a screen
/// could show that a birth was registered and an alert acknowledged without
/// being able to name a person for either. In a register whose value is that
/// it can be questioned, that is the gap worth closing.
/// </summary>
public class NamedActorTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid NurseId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid OfficerId = Guid.Parse("0199a1b2-1003-7000-8000-000000000003");
    private static readonly Guid GhostId = Guid.Parse("0199a1b2-9999-7000-8000-000000000009");

    private const string District = "SS-CE-TER";
    private const string OfficerSubject = "33333333-3333-4333-8333-333333333333";
    private const string Brn = "100001";
    private const string GhostBrn = "100002";

    private static readonly DateTime Born = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public NamedActorTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId,
            Name = "Terekeka Village Health Post",
            CountyCode = District,
            ConnectivityProfile = ConnectivityProfile.OfflineFirst,
        });

        db.Registrars.AddRange(
            new Registrar { RegistrarId = NurseId, FacilityId = FacilityId, ExternalSubjectId = AuthTestContext.DefaultSubject, DisplayName = "Nurse A. Lado" },
            new Registrar { RegistrarId = OfficerId, FacilityId = FacilityId, ExternalSubjectId = OfficerSubject, DisplayName = "Nyandeng Wani", Role = RegistrarRole.DistrictOfficer });

        db.BirthRecords.Add(Record(Brn, NurseId));

        db.SaveChanges();
    }

    // ---- who registered the birth -----------------------------------------

    [Fact]
    public async Task A_record_names_the_registrar_who_filed_it()
    {
        var record = Ok(await GetRecordAsync(Brn));

        Assert.Equal(NurseId, record.RegisteredByRegistrarId);
        Assert.Equal("Nurse A. Lado", record.RegisteredByRegistrarName);
    }

    [Fact]
    public void A_record_can_never_point_at_a_registrar_who_does_not_exist()
    {
        // The FK is enforced, so the name is always resolvable -- which is
        // why the response does not need to explain an absent one.
        //
        // Note what enforcing it this way costs: the relationship cascades,
        // so deleting a registrar deletes every birth they filed. In a
        // register where an annulment keeps the record and the audit trail
        // cannot be rewritten, that is the one delete nobody intended.
        using var db = new NcbrsDbContext(_options);

        db.BirthRecords.Add(Record(GhostBrn, GhostId));

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task The_timestamp_is_named_for_what_it_is()
    {
        // Server receipt, not the device's capture time -- which is computed,
        // used to decide lateness, and then discarded. Publishing receipt
        // under the more useful label would misdescribe a record synced weeks
        // after the family was seen.
        var record = Ok(await GetRecordAsync(Brn));

        Assert.NotNull(record.ReceivedAtUtc);
    }

    // ---- who acknowledged the alert ---------------------------------------

    [Fact]
    public async Task Acknowledging_an_alert_names_the_person_who_did_it()
    {
        // Acknowledging is not resolving (WS-F4): it says "I am driving out
        // there Thursday". An undertaking nobody is named for is one nobody
        // can be asked about on Friday.
        var alertId = await GivenAnAlertAsync();

        var acknowledged = OkAlert(await AcknowledgeAsync(alertId));

        Assert.Equal(OfficerId, acknowledged.AcknowledgedByRegistrarId);
        Assert.Equal("Nyandeng Wani", acknowledged.AcknowledgedByRegistrarName);
    }

    [Fact]
    public async Task The_queue_names_the_acknowledger_too_not_only_the_response()
    {
        // The response to pressing the button is the easy half. The queue is
        // where a district reads who undertook what, days later.
        var alertId = await GivenAnAlertAsync();
        await AcknowledgeAsync(alertId);

        var alerts = OkAlerts(await ListAlertsAsync());
        var alert = Assert.Single(alerts);

        Assert.Equal("Nyandeng Wani", alert.AcknowledgedByRegistrarName);
    }

    [Fact]
    public async Task An_unacknowledged_alert_names_nobody()
    {
        // Null rather than an empty string: nobody has undertaken anything,
        // which is different from an undertaking by someone unnamed.
        await GivenAnAlertAsync();

        var alerts = OkAlerts(await ListAlertsAsync());
        var alert = Assert.Single(alerts);

        Assert.Null(alert.AcknowledgedByRegistrarId);
        Assert.Null(alert.AcknowledgedByRegistrarName);
    }

    private async Task<Guid> GivenAnAlertAsync()
    {
        await using var db = new NcbrsDbContext(_options);

        var alert = new DeviceAlert
        {
            DeviceId = "TABLET-01",
            FacilityId = FacilityId,
            DistrictId = District,
            Kind = DeviceAlertKind.Silent,
            Status = DeviceAlertStatus.Open,
            RaisedAtUtc = DateTime.UtcNow,
            DaysSilentWhenRaised = 30,
            ThresholdDays = 21,
        };

        db.DeviceAlerts.Add(alert);
        await db.SaveChangesAsync();

        return alert.DeviceAlertId;
    }

    private async Task<ActionResult<DeviceAlertResponse>> AcknowledgeAsync(Guid alertId)
    {
        await using var db = new NcbrsDbContext(_options);
        var http = AuthTestContext.HttpContextFor(OfficerSubject, NcbrsRoles.DistrictOfficer);

        return await DevicesController(db, http).AcknowledgeAlert(
            alertId,
            new ApiRequest<AcknowledgeDeviceAlertRequest>
            {
                Data = new AcknowledgeDeviceAlertRequest { Note = "Driving out Thursday." },
            });
    }

    private async Task<ActionResult<IReadOnlyList<DeviceAlertResponse>>> ListAlertsAsync()
    {
        await using var db = new NcbrsDbContext(_options);
        var http = AuthTestContext.HttpContextFor(OfficerSubject, NcbrsRoles.DistrictOfficer);

        return await DevicesController(db, http).Alerts();
    }

    private async Task<ActionResult<BirthRecordResponse>> GetRecordAsync(string brn)
    {
        await using var db = new NcbrsDbContext(_options);
        var http = AuthTestContext.HttpContextFor();

        return await RecordsController(db, http).GetByBrn(brn);
    }

    /// <summary>
    /// Only the read path is exercised here, so the services it is handed are
    /// real but unused -- GetByBrn queries the context directly.
    /// </summary>
    private static BirthRecordsController RecordsController(NcbrsDbContext db, HttpContext http)
    {
        var current = AuthTestContext.RegistrarService(db, http);
        var districts = new CountyLookup(db);

        return new BirthRecordsController(
            db,
            new BirthRegistrationService(
                db,
                new NoOpEventPublisher(),
                current,
                new DuplicateDetectionService(
                    db,
                    new DuplicateMatcher(),
                    new CertificateRevocationRecorder(db),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DuplicateDetectionService>.Instance,
                    districts),
                districts,
                Microsoft.Extensions.Options.Options.Create(new StatutoryRegistrationOptions())),
            new AmendmentService(
                db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current, districts),
            current,
            districts,
            Microsoft.Extensions.Options.Options.Create(new StatutoryRegistrationOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static DevicesController DevicesController(NcbrsDbContext db, HttpContext http) =>
        new(db, AuthTestContext.RegistrarService(db, http), new CountyLookup(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

    private static BirthRecordResponse Ok(ActionResult<BirthRecordResponse> result) =>
        Assert.IsType<BirthRecordResponse>(result.Value);

    private static DeviceAlertResponse OkAlert(ActionResult<DeviceAlertResponse> result) =>
        Assert.IsType<DeviceAlertResponse>(result.Value);

    private static IReadOnlyList<DeviceAlertResponse> OkAlerts(
        ActionResult<IReadOnlyList<DeviceAlertResponse>> result) =>
        Assert.IsAssignableFrom<IReadOnlyList<DeviceAlertResponse>>(result.Value);

    private static BirthRecord Record(string brn, Guid registrarId)
        => new()
        {
            Brn = brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Ayen Deng" },
            FacilityId = FacilityId,
            RegisteredByRegistrarId = registrarId,
            DateOfBirth = Born,
            Sex = Sex.Female,
            BirthWeightGrams = 3200,
            Plurality = BirthPlurality.Singleton,
            Status = RecordStatus.Confirmed,
        };

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
