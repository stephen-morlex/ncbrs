using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The register keeps the moment a birth was registered on a device, not only
/// the moment it reached the centre.
///
/// The two are the same instant for a hospital terminal and three weeks apart
/// for a village post, and only the first says anything about when the birth
/// was actually registered. It is the timestamp the statutory window is
/// measured to, so it is the input to the decision that withholds a
/// certificate until a district registrar has verified evidence — and until
/// now it was computed, checked, used for exactly that, and discarded.
///
/// What that cost: a family told their registration was late could not be
/// shown why, because the value the finding rested on no longer existed
/// anywhere, and recomputing it from the arrival time gives a different
/// answer for every record the offline tier ever produced.
/// </summary>
public class DeviceCaptureTimeTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private const int WindowDays = 90;

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public DeviceCaptureTimeTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId,
            Name = "Terekeka Village Health Post",
            CountyCode = "SS-CE-TER",
            BrnBlockStart = 100_000,
            BrnBlockEnd = 199_999,
            BrnBlockNextAvailable = 100_000
        });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Lado",
            CredentialHash = "test"
        });

        db.SaveChanges();
    }

    [Fact]
    public async Task A_post_that_was_offline_keeps_the_day_it_registered_the_birth()
    {
        // Born 100 days ago, registered on the tablet 10 days later, and only
        // now reaching the centre. The tablet was out of contact for three
        // months, which is the tier this system exists for.
        var capturedAt = DateTime.UtcNow.Date.AddDays(-90);

        var result = await RegisterAsync(Request("100001", bornDaysAgo: 100, capturedAt: capturedAt));

        Assert.True(result.Succeeded);

        var record = await StoredAsync("100001");

        Assert.NotNull(record.RegisteredAtUtc);
        Assert.Equal(capturedAt, record.RegisteredAtUtc);

        // The point of storing it: it is not the arrival time, and nothing
        // could have recovered it from the arrival time.
        Assert.True(record.CreatedAtUtc - record.RegisteredAtUtc!.Value > TimeSpan.FromDays(89));
    }

    [Fact]
    public async Task An_online_registration_stores_both_and_they_agree()
    {
        // No capture time sent, because for a hospital terminal there is no
        // second moment to report. The column is still written: "registered
        // when it arrived" is a fact about this record, and must not be
        // recorded the same way as "we no longer know".
        var result = await RegisterAsync(Request("100002", bornDaysAgo: 3));

        Assert.True(result.Succeeded);

        var record = await StoredAsync("100002");

        Assert.NotNull(record.RegisteredAtUtc);
        Assert.True((record.CreatedAtUtc - record.RegisteredAtUtc!.Value).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task The_window_the_record_was_judged_against_survives_a_change_in_the_law()
    {
        // Filed while the Act said 30 days.
        var first = await RegisterAsync(Request("100003", bornDaysAgo: 10), windowDays: 30);
        Assert.True(first.Succeeded);

        // The Act is amended to 180. Nothing about the earlier registration
        // changed, and reading it must not suggest otherwise -- recomputing
        // lateness against today's window would silently restate what was on
        // time years ago.
        var second = await RegisterAsync(Request("100004", bornDaysAgo: 10), windowDays: 180);
        Assert.True(second.Succeeded);

        Assert.Equal(30, (await StoredAsync("100003")).StatutoryWindowDays);
        Assert.Equal(180, (await StoredAsync("100004")).StatutoryWindowDays);
    }

    [Fact]
    public async Task A_registration_kept_on_time_by_the_devices_own_clock_is_audited()
    {
        // On time measured to the device (10 days), late measured to arrival
        // (100 days). The device's word is what kept this out of the evidence
        // process, and that is the case StatutoryRegistrationOptions calls the
        // residual: bounded below by the birth and above by the server clock,
        // free in between.
        var result = await RegisterAsync(
            Request("100005", bornDaysAgo: 100, capturedAt: DateTime.UtcNow.Date.AddDays(-90)));

        Assert.True(result.Succeeded);

        // The registration still succeeds and is still on time. Nothing here
        // is an accusation -- a post genuinely out of contact for months looks
        // exactly like this, and refusing it would close the offline tier.
        Assert.Null(result.LateRegistration);

        var audited = await ActionsAsync("100005");

        var residual = Assert.Single(audited, action => action.StartsWith("StatutoryWindowMetOnDeviceTime"));

        // Both numbers, so the trail says how far apart the two clocks put
        // this record rather than only that they disagreed.
        Assert.Equal("StatutoryWindowMetOnDeviceTime:10/100", residual);
    }

    [Fact]
    public async Task An_ordinary_on_time_registration_is_not_audited_as_a_residual()
    {
        // The negative that gives the signal its value. If every registration
        // wrote one of these rows, a district reviewing them would learn
        // nothing and would stop reviewing them.
        var result = await RegisterAsync(Request("100006", bornDaysAgo: 3));

        Assert.True(result.Succeeded);

        var audited = await ActionsAsync("100006");

        // Not vacuous: the registration does audit other things, so the
        // absence below is this row missing rather than the query finding
        // nothing at all.
        Assert.NotEmpty(audited);

        Assert.DoesNotContain(audited, action => action.StartsWith("StatutoryWindowMetOnDeviceTime"));
    }

    [Fact]
    public async Task A_birth_late_on_both_clocks_is_not_audited_as_a_residual()
    {
        // Late by 100 days on the device's own reckoning, so it went through
        // the evidence process the ordinary way. The device's clock avoided
        // nothing, and recording it as though it had would put the honest
        // late registrations -- the large majority of rural births -- into the
        // queue meant for the ones that dodged.
        var result = await RegisterAsync(Request(
            "100007",
            bornDaysAgo: 200,
            capturedAt: DateTime.UtcNow.Date.AddDays(-100),
            late: new LateRegistrationDetails
            {
                EvidenceType = LateRegistrationEvidenceType.BirthAttendantAttestation,
                EvidenceReference = "TBA attestation 44/2026",
                DeclarantName = "Nyandeng Deng",
                DeclarantRelationship = "mother"
            }));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.LateRegistration);

        var audited = await ActionsAsync("100007");

        // It is audited, just as the thing it is: a late registration filed
        // with evidence. That is what makes the absence of the residual row
        // meaningful rather than an empty query.
        Assert.Contains(audited, action => action == "LateRegistrationFiled");

        Assert.DoesNotContain(audited, action => action.StartsWith("StatutoryWindowMetOnDeviceTime"));
    }

    [Fact]
    public async Task ThePublishedEventCarriesTheDeviceTime_NotOnlyThePublishTime()
    {
        // The registry keeping the capture time is only half of it. Until the
        // event carried it too, the reporting side could measure the delay
        // between a birth and the centre hearing about it but could not say
        // how much of that was the family and how much was the link -- and
        // for the offline tier those need different people to fix them.
        var capturedAt = DateTime.UtcNow.Date.AddDays(-90);

        await using var db = NewDb();
        var publisher = new NoOpEventPublisher();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var registrations = new BirthRegistrationService(
            db, publisher, current,
            new DuplicateDetectionService(db, new DuplicateMatcher(),
                new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance,
                new CountyLookup(db)),
            new CountyLookup(db),
            Options.Create(new StatutoryRegistrationOptions { WindowDays = WindowDays }));

        var result = await registrations.RegisterAsync(
            Request("100008", bornDaysAgo: 100, capturedAt: capturedAt),
            registrar,
            Guid.CreateVersion7());

        Assert.True(result.Succeeded);

        var published = Assert.Single(publisher.Registrations);

        Assert.Equal(capturedAt, published.RegisteredAtUtc);

        // And the two are genuinely different instants on the event, which is
        // the only reason carrying both is worth anything.
        Assert.NotEqual(published.EventTimestampUtc, published.RegisteredAtUtc);
    }

    private async Task<BirthRecord> StoredAsync(string brn)
    {
        await using var db = NewDb();

        return await db.BirthRecords.SingleAsync(record => record.Brn == brn);
    }

    private async Task<List<string>> ActionsAsync(string brn)
    {
        await using var db = NewDb();

        return await db.AuditLogs
            .Where(log => log.EntityId == brn)
            .Select(log => log.Action)
            .ToListAsync();
    }

    private NcbrsDbContext NewDb() => new(_options);

    private static RegisterBirthRequest Request(
        string brn,
        int bornDaysAgo,
        DateTime? capturedAt = null,
        LateRegistrationDetails? late = null)
        => new()
        {
            Brn = brn,
            FacilityId = FacilityId,
            ChildFullName = "Ayen Deng",
            DateOfBirth = DateTime.UtcNow.Date.AddDays(-bornDaysAgo),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            BirthOrder = 1,
            DeviceId = "TABLET-07",
            RegisteredAtUtc = capturedAt,
            LateRegistration = late
        };

    private async Task<RegistrationResult> RegisterAsync(
        RegisterBirthRequest request, int windowDays = WindowDays)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var registrations = new BirthRegistrationService(
            db, new NoOpEventPublisher(), current,
            new DuplicateDetectionService(db, new DuplicateMatcher(),
                new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance,
                new CountyLookup(db)),
            new CountyLookup(db),
            Options.Create(new StatutoryRegistrationOptions { WindowDays = windowDays }));

        return await registrations.RegisterAsync(request, registrar, Guid.CreateVersion7());
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
