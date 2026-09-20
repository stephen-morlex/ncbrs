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
/// W4. Reading the audit trail through the API.
///
/// The load-bearing claims: the trail does not disclose what the record
/// itself withholds, a read of the trail is written to the trail, and an
/// entry names the person rather than their id.
/// </summary>
public class AuditQueryTests : IDisposable
{
    private static readonly Guid TerekekaFacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid JubaFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");

    private static readonly Guid NurseId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid OfficerId = Guid.Parse("0199a1b2-1003-7000-8000-000000000003");
    private static readonly Guid MinistryId = Guid.Parse("0199a1b2-1004-7000-8000-000000000004");
    private static readonly Guid JubaNurseId = Guid.Parse("0199a1b2-1005-7000-8000-000000000005");

    private const string TerekekaDistrict = "SS-CE-TER";
    private const string JubaDistrict = "SS-CE-JUB";
    private const string OfficerSubject = "33333333-3333-4333-8333-333333333333";
    private const string MinistrySubject = "44444444-4444-4444-8444-444444444444";

    private const string CentralBrn = "100001";
    private const string JubaBrn = "200001";

    private static readonly DateTime Born = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public AuditQueryTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = TerekekaFacilityId, Name = "Terekeka Village Health Post", CountyCode = TerekekaDistrict },
            new Facility { FacilityId = JubaFacilityId, Name = "Juba Central", CountyCode = JubaDistrict });

        db.Registrars.AddRange(
            new Registrar { RegistrarId = NurseId, FacilityId = TerekekaFacilityId, ExternalSubjectId = AuthTestContext.DefaultSubject, DisplayName = "Nurse A. Lado" },
            new Registrar { RegistrarId = OfficerId, FacilityId = TerekekaFacilityId, ExternalSubjectId = OfficerSubject, DisplayName = "Nyandeng Wani", Role = RegistrarRole.DistrictOfficer },
            new Registrar { RegistrarId = MinistryId, FacilityId = TerekekaFacilityId, ExternalSubjectId = MinistrySubject, DisplayName = "Aluel Lako", Role = RegistrarRole.MinistryAdmin },
            new Registrar { RegistrarId = JubaNurseId, FacilityId = JubaFacilityId, ExternalSubjectId = "55555555-5555-4555-8555-555555555555", DisplayName = "Thandi Nkosi" });

        db.BirthRecords.AddRange(
            Record(CentralBrn, TerekekaFacilityId, NurseId),
            Record(JubaBrn, JubaFacilityId, JubaNurseId));

        db.AuditLogs.AddRange(
            Entry("BirthRecord", CentralBrn, "Create", NurseId, Earlier),
            Entry("BirthRecord", CentralBrn, "Amend", OfficerId, Later),
            Entry("BirthRecord", JubaBrn, "Create", JubaNurseId, Later, JubaDistrict),
            Entry("BirthRecordSearch", TerekekaDistrict, "Search:name=Lado;returned=2;total=2", NurseId, Later),
            // Nobody did this: a sweep raised it. The actor name must come
            // back null rather than blank.
            Entry("DeviceAlert", "TABLET-01", "Raise:Silent", actor: null, Later));

        db.SaveChanges();
    }

    // ---- what the trail must not disclose ---------------------------------

    [Fact]
    public async Task Asking_for_another_districts_record_is_not_found()
    {
        // The trail must not answer what the record itself withholds. A 200
        // with entries -- or a 403 -- would confirm the BRN exists elsewhere.
        var result = await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], brn: JubaBrn);

        AssertStatus(StatusCodes.Status404NotFound, result.Result);
    }

    [Fact]
    public async Task An_unknown_brn_answers_the_same_way()
    {
        var result = await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], brn: "999999");

        AssertStatus(StatusCodes.Status404NotFound, result.Result);
    }

    [Fact]
    public async Task Unfiltered_the_trail_is_scoped_to_the_callers_own_people()
    {
        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer]));

        Assert.DoesNotContain(page.Items, entry => entry.ActorId == JubaNurseId);
        Assert.Contains(page.Items, entry => entry.ActorId == NurseId);
    }

    [Fact]
    public async Task The_ministry_sees_every_district()
    {
        var page = Ok(await GetAsync(MinistrySubject, [NcbrsRoles.MinistryAdmin]));

        Assert.Contains(page.Items, entry => entry.ActorId == JubaNurseId);
    }

    [Fact]
    public async Task Naming_another_district_is_refused()
    {
        var result = await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer],
            districtId: JubaDistrict);

        AssertStatus(StatusCodes.Status403Forbidden, result.Result);
    }

    // ---- the read is itself recorded --------------------------------------

    [Fact]
    public async Task Reading_the_trail_is_written_to_the_trail()
    {
        // RequestLog records the call but not who made it, so without this
        // row the one privileged read in the system would be the one act it
        // could not account for.
        await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer]);

        await using var db = new NcbrsDbContext(_options);
        var read = await db.AuditLogs.SingleAsync(entry => entry.EntityType == "AuditTrail");

        Assert.Equal(OfficerId, read.UserId);
        Assert.Equal(TerekekaDistrict, read.EntityId);
    }

    [Fact]
    public async Task The_row_records_what_was_asked_of_the_trail()
    {
        // "Read the trail" and "read every entry for this one registrar" are
        // different acts, and only the second looks like checking up on a
        // colleague.
        await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], actorId: NurseId);

        await using var db = new NcbrsDbContext(_options);
        var read = await db.AuditLogs.SingleAsync(entry => entry.EntityType == "AuditTrail");

        Assert.Contains($"actor={NurseId}", read.Action);
    }

    [Fact]
    public async Task A_refused_read_writes_nothing()
    {
        // Nothing was disclosed, so there is nothing to account for, and a
        // row would fill the trail with non-events.
        await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], brn: JubaBrn);

        await using var db = new NcbrsDbContext(_options);

        Assert.False(await db.AuditLogs.AnyAsync(entry => entry.EntityType == "AuditTrail"));
    }

    // ---- what an entry says -----------------------------------------------

    [Fact]
    public async Task An_entry_names_the_actor_rather_than_their_id()
    {
        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], brn: CentralBrn));

        Assert.Contains(page.Items, entry => entry.ActorName == "Nurse A. Lado");
        Assert.Contains(page.Items, entry => entry.ActorName == "Nyandeng Wani");
    }

    [Fact]
    public async Task An_entry_no_person_caused_has_no_actor_name()
    {
        // A sweep raised it. Null says "not a person here", which is
        // different from a blank name.
        var page = Ok(await GetAsync(MinistrySubject, [NcbrsRoles.MinistryAdmin],
            entityType: "DeviceAlert"));

        var entry = Assert.Single(page.Items);

        Assert.Null(entry.ActorId);
        Assert.Null(entry.ActorName);
    }

    [Fact]
    public async Task A_records_trail_includes_every_actor_not_only_the_callers_own()
    {
        // Once the record is established as the caller's to see, the question
        // is what happened to it -- withholding an actor would answer it
        // misleadingly.
        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], brn: CentralBrn));

        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task The_search_audit_written_by_W1_is_readable_here()
    {
        // The point of doing W4 after W1: those rows existed and nobody
        // without database access could see them.
        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer],
            entityType: "BirthRecordSearch"));

        var entry = Assert.Single(page.Items);

        Assert.Contains("name=Lado", entry.Action);
    }

// ---- the district column ---------------------------------------------

    [Fact]
    public async Task A_row_written_before_the_column_existed_still_reaches_its_district()
    {
        // Those rows carry AuditLog.Unknown and can never be given a district
        // -- the table is append-only and enforced so at the database. The
        // actor's district is the best that can be said of them, and losing
        // them from the district's view entirely would be worse than an
        // approximation.
        await using (var seed = new NcbrsDbContext(_options))
        {
            seed.AuditLogs.Add(Entry("BirthRecord", CentralBrn, "LegacyAct", NurseId, Later,
                AuditLog.Unknown));

            await seed.SaveChangesAsync();
        }

        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer]));

        Assert.Contains(page.Items, entry => entry.Action == "LegacyAct");
    }

    [Fact]
    public async Task A_legacy_row_by_an_outsider_stays_invisible()
    {
        // The permanent hole, asserted so it is not mistaken for a bug later:
        // an act on this district's records by someone outside it, written
        // before the column existed, cannot be attributed and does not appear.
        await using (var seed = new NcbrsDbContext(_options))
        {
            seed.AuditLogs.Add(Entry("BirthRecord", CentralBrn, "LegacyOutsiderAct",
                JubaNurseId, Later, AuditLog.Unknown));

            await seed.SaveChangesAsync();
        }

        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer]));

        Assert.DoesNotContain(page.Items, entry => entry.Action == "LegacyOutsiderAct");
    }

    [Fact]
    public async Task A_new_row_by_an_outsider_does_reach_the_district()
    {
        // What the column buys. A ministry admin acting on this district's
        // record produces a row the district can now see -- which under
        // actor-scoping alone it never could.
        await using (var seed = new NcbrsDbContext(_options))
        {
            seed.AuditLogs.Add(Entry("BirthRecord", CentralBrn, "AnnulByMinistry",
                MinistryId, Later, TerekekaDistrict));

            await seed.SaveChangesAsync();
        }

        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer]));

        Assert.Contains(page.Items, entry => entry.Action == "AnnulByMinistry");
    }

    // ---- filtering and ordering -------------------------------------------

    [Fact]
    public async Task A_date_range_excludes_entries_outside_it()
    {
        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer],
            from: Later.AddHours(-1)));

        Assert.DoesNotContain(page.Items, entry => entry.TimestampUtc == Earlier);
    }

    [Fact]
    public async Task A_backwards_date_range_is_refused()
    {
        var result = await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer],
            from: Later, to: Earlier);

        AssertStatus(StatusCodes.Status400BadRequest, result.Result);
    }

    [Fact]
    public async Task Entries_are_most_recent_first()
    {
        // Someone opening the trail is almost always asking what just
        // happened.
        var page = Ok(await GetAsync(OfficerSubject, [NcbrsRoles.DistrictOfficer], brn: CentralBrn));

        Assert.Equal(["Amend", "Create"], page.Items.Select(entry => entry.Action));
    }

    private async Task<ActionResult<Page<AuditEntryResponse>>> GetAsync(
        string subject,
        string[] roles,
        string? brn = null,
        Guid? actorId = null,
        string? entityType = null,
        DateTime? from = null,
        DateTime? to = null,
        string? districtId = null)
    {
        await using var db = new NcbrsDbContext(_options);

        var http = AuthTestContext.HttpContextFor(subject, roles);

        var controller = new AuditController(
            db,
            AuthTestContext.RegistrarService(db, http),
            new CountyScopeResolver(new CountyLookup(db)),
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        return await controller.Get(brn, actorId, deviceId: null, entityType, from, to, districtId);
    }

    private static Page<AuditEntryResponse> Ok(ActionResult<Page<AuditEntryResponse>> result) =>
        Assert.IsType<Page<AuditEntryResponse>>(result.Value);

    private static void AssertStatus(int expected, ActionResult? result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);

        Assert.Equal(expected, objectResult.StatusCode);
    }

    private static AuditLog Entry(
        string entityType, string entityId, string action, Guid? actor, DateTime at,
        string district = TerekekaDistrict)
        => new()
        {
            EntityType = entityType,
            EntityId = entityId,
            DistrictId = district,
            Action = action,
            UserId = actor,
            DeviceId = "web",
            TimestampUtc = at
        };

    private static BirthRecord Record(string brn, Guid facilityId, Guid registrarId)
        => new()
        {
            Brn = brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Ayen Deng" },
            FacilityId = facilityId,
            RegisteredByRegistrarId = registrarId,
            DateOfBirth = Born,
            Sex = Sex.Female,
            BirthWeightGrams = 3200,
            Plurality = BirthPlurality.Singleton,
            Status = RecordStatus.Confirmed
        };

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
