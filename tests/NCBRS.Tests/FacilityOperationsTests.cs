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
/// The operations view (plan §17 item 35): one row per facility of what needs
/// someone's attention. What these pin: each figure counts what its queue
/// would list, by the same definition; a correction is one request however
/// many fields it touches; refusals unknown for lack of recorded outcomes are
/// null, never zero; and each caller sees the facilities they act for.
/// </summary>
public class FacilityOperationsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Terekeka = Guid.Parse("0199a1b2-0001-7000-8000-00000000d001");
    private static readonly Guid Tali = Guid.Parse("0199a1b2-0002-7000-8000-00000000d002");
    private static readonly Guid Juba = Guid.Parse("0199a1b2-0003-7000-8000-00000000d003");
    private static readonly Guid Unknown = Guid.Parse("0199a1b2-0004-7000-8000-00000000d004");
    private static readonly Guid Registrar = Guid.Parse("0199a1b2-1001-7000-8000-00000000d001");

    private const string OfficerSubject = "officer-terekeka";
    private const string MinistrySubject = "ministry";

    private readonly TestDatabase _database = TestDatabase.Create();

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    public FacilityOperationsTests()
    {
        using var db = NewDb();
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = Terekeka, Name = "Terekeka Village Health Post", CountyCode = "SS0104", ConnectivityProfile = ConnectivityProfile.OfflineFirst },
            new Facility { FacilityId = Tali, Name = "Tali PHCU", CountyCode = "SS0104" },
            new Facility { FacilityId = Juba, Name = "Juba Teaching Hospital", CountyCode = "SS0101" },
            new Facility { FacilityId = Unknown, Name = "Mangala PHCU", CountyCode = "SS0104" });

        db.Registrars.AddRange(
            new Registrar { RegistrarId = Registrar, FacilityId = Terekeka, ExternalSubjectId = AuthTestContext.DefaultSubject, DisplayName = "Nurse Lado", CredentialHash = "x" },
            new Registrar { FacilityId = Terekeka, ExternalSubjectId = OfficerSubject, DisplayName = "Officer", CredentialHash = "x", Role = RegistrarRole.DistrictOfficer },
            new Registrar { FacilityId = Juba, ExternalSubjectId = MinistrySubject, DisplayName = "Ministry", CredentialHash = "x", Role = RegistrarRole.MinistryAdmin });

        // Terekeka: two registrations this month and one long ago; one still
        // provisional; one number the registry could not confirm; one
        // certificate printed and waiting.
        var recent = Record("100001", Now.AddDays(-2));
        var recentToo = Record("100002", Now.AddDays(-10));
        recentToo.Certificates.Add(new Certificate { IssueDateUtc = Now.AddDays(-5), SignatureHash = "s", QrPayload = "q" });
        var old = Record("100003", Now.AddDays(-200));
        old.Certificates.Add(new Certificate
        {
            IssueDateUtc = Now.AddDays(-190), SignatureHash = "s", QrPayload = "q",
            HandedOverAtUtc = Now.AddDays(-180), HandedOverTo = CertificateRecipient.Mother,
        });
        var provisional = Record("PROV-TAB-01-000001", Now.AddDays(-40));
        provisional.ProvisionalIdentifier = provisional.Brn;
        provisional.ConfirmedAtUtc = null;
        var unconfirmed = Record("990001", Now.AddDays(-50));
        unconfirmed.ConfirmedAtUtc = null;
        unconfirmed.LateRegistration = new LateRegistration
        {
            DaysLate = 120, WindowDaysAtFiling = 90, EvidenceType = LateRegistrationEvidenceType.HealthFacilityRecord,
            DeclarantName = "Mary", DeclarantRelationship = "Mother", SubmittedByRegistrarId = Registrar, SubmittedAtUtc = Now.AddDays(-49),
        };
        db.BirthRecords.AddRange(recent, recentToo, old, provisional, unconfirmed);

        // One correction of two fields, and another of one: two requests.
        var request = Guid.CreateVersion7();
        db.BirthRecordAmendments.AddRange(
            Amendment(recent, request, "ChildFullName", Now.AddDays(-6)),
            Amendment(recent, request, "Sex", Now.AddDays(-6)),
            Amendment(recentToo, Guid.CreateVersion7(), "DateOfBirth", Now.AddDays(-3)),
            Amendment(recentToo, Guid.CreateVersion7(), "BirthWeightGrams", Now.AddDays(-1), AmendmentStatus.Applied));

        db.DuplicateCandidates.Add(new DuplicateCandidate
        {
            BirthRecord = recent, MatchedBirthRecord = recentToo, Score = 80, Reasons = "name", DetectedAtUtc = Now.AddDays(-4),
        });

        db.Devices.AddRange(
            Device("TAB-1", Terekeka, DeviceStatus.Enrolled),
            Device("TAB-2", Terekeka, DeviceStatus.Revoked));
        db.DeviceAlerts.AddRange(
            new DeviceAlert { DeviceId = "TAB-1", FacilityId = Terekeka, CountyCode = "SS0104", Kind = DeviceAlertKind.Silent },
            new DeviceAlert { DeviceId = "TAB-1", FacilityId = Terekeka, CountyCode = "SS0104", Kind = DeviceAlertKind.Silent, Status = DeviceAlertStatus.Resolved, ResolvedAtUtc = Now.AddDays(-30) });

        // A batch from before outcomes were kept, and one after; and one outside the window.
        db.SyncBatches.AddRange(
            Batch(Terekeka, Now.AddDays(-20), 5, rejected: null),
            Batch(Terekeka, Now.AddDays(-1), 10, rejected: 2),
            Batch(Terekeka, Now.AddDays(-60), 7, rejected: 7),
            // Mangala synced this month, but only before outcomes were kept: unknown.
            Batch(Unknown, Now.AddDays(-3), 4, rejected: null));

        db.BirthRecords.Add(Record("200001", Now.AddDays(-1), Juba));

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static BirthRecord Record(string brn, DateTime createdAt, Guid? facility = null)
        => new()
        {
            Brn = brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = $"Child {brn}" },
            FacilityId = facility ?? Terekeka,
            RegisteredByRegistrarId = Registrar,
            DateOfBirth = createdAt.Date.AddDays(-1),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            CreatedAtUtc = createdAt,
            ConfirmedAtUtc = createdAt,
        };

    private static BirthRecordAmendment Amendment(
        BirthRecord record, Guid request, string field, DateTime at, AmendmentStatus status = AmendmentStatus.PendingApproval)
        => new()
        {
            BirthRecord = record, AmendmentRequestId = request, Field = field, Reason = "Typo",
            AmendedByRegistrarId = Registrar, AmendedAtUtc = at, Status = status,
        };

    private static Device Device(string id, Guid facility, DeviceStatus status)
        => new() { DeviceId = id, FacilityId = facility, Status = status, PublicKeyPem = "key", EnrolledByRegistrarId = Registrar };

    private static SyncBatch Batch(Guid facility, DateTime at, int records, int? rejected)
        => new()
        {
            DeviceId = "TAB-1", FacilityId = facility, SubmittedAtUtc = at, RecordCount = records,
            Status = SyncBatchStatus.Reconciled,
            RegisteredCount = rejected is null ? null : records - rejected,
            DuplicateCount = rejected is null ? null : 0,
            RejectedCount = rejected,
        };

    private async Task<FacilityOperationsReport> ReportAsync(Guid? facility = null, string? county = null)
    {
        await using var db = NewDb();
        return await new FacilityOperationsService(db, new FixedClock(Now)).ReportAsync(facility, county);
    }

    [Fact]
    public async Task AFacilitysRowCountsWhatEachQueueWouldList()
    {
        var row = (await ReportAsync(Terekeka)).Facilities.Single();

        Assert.Equal(2, row.RegistrationsInWindow);
        Assert.Equal(Now.AddDays(-1), row.LastSyncAtUtc);
        Assert.Equal(1, row.ActiveDevices);          // the revoked tablet is not counted
        Assert.Equal(1, row.OpenDeviceAlerts);       // the resolved alert is not counted
        Assert.Equal(1, row.ProvisionalOpen);
        Assert.Equal(1, row.UnconfirmedNumbers);
        Assert.Equal(new Backlog(2, Now.AddDays(-6)), row.PendingCorrections);
        Assert.Equal(new Backlog(1, Now.AddDays(-49)), row.PendingLateRegistrations);
        Assert.Equal(new Backlog(1, Now.AddDays(-4)), row.PendingDuplicates);
        Assert.Equal(1, row.CertificatesToHandOver);
        Assert.Equal(1, row.CertificatesToIssue);   // "recent": the provisional and late ones wait on the registry
        Assert.Equal(Now.AddDays(-50), row.OldestUncollectedRegisteredAtUtc);
    }

    /// <summary>
    /// The certificate figures are the certificate list's own definition: the
    /// row and the list a district officer opens from it can never disagree.
    /// </summary>
    [Fact]
    public async Task TheCertificateFiguresAgreeWithTheCertificateList()
    {
        var row = (await ReportAsync(Terekeka)).Facilities.Single();

        await using var db = NewDb();
        var list = await new CertificateQueueService(db).ListAsync(Terekeka, null, null, new PageRequest());

        Assert.Equal(list.Items.Count(item => item.Stage == CertificateQueueStage.ToIssue), row.CertificatesToIssue);
        Assert.Equal(list.Items.Count(item => item.Stage == CertificateQueueStage.ToHandOver), row.CertificatesToHandOver);
    }

    /// <summary>Only batches that kept their outcomes count; the one outside the window does not.</summary>
    [Fact]
    public async Task RefusalsComeFromBatchesThatRecordedThem()
    {
        var row = (await ReportAsync(Terekeka)).Facilities.Single();

        Assert.Equal(10, row.SyncedRecordsInWindow);
        Assert.Equal(2, row.RefusedRecordsInWindow);
    }

    /// <summary>
    /// A facility that synced only before outcomes were kept has refusals that
    /// are unknown. Zero would claim its births went cleanly.
    /// </summary>
    [Fact]
    public async Task RefusalsNobodyRecordedAreUnknownNotZero()
    {
        var row = (await ReportAsync(Unknown)).Facilities.Single();

        Assert.Null(row.SyncedRecordsInWindow);
        Assert.Null(row.RefusedRecordsInWindow);
        Assert.NotNull(row.LastSyncAtUtc);
    }

    [Fact]
    public async Task AQuietFacilityReportsZerosAndNeverSynced()
    {
        var row = (await ReportAsync(Tali)).Facilities.Single();

        Assert.Equal(0, row.RegistrationsInWindow);
        Assert.Null(row.LastSyncAtUtc);
        Assert.Equal(new Backlog(0, null), row.PendingCorrections);
        Assert.Null(row.OldestUncollectedRegisteredAtUtc);
    }

    [Fact]
    public async Task ACountyListsItsFacilitiesByName()
    {
        var report = await ReportAsync(county: "SS0104");

        Assert.Equal(["Mangala PHCU", "Tali PHCU", "Terekeka Village Health Post"], report.Facilities.Select(row => row.Name));
        Assert.Equal(30, report.WindowDays);
        Assert.Equal(Now, report.GeneratedAtUtc);
    }

    // --- who sees what -------------------------------------------------------------------------

    private async Task<ActionResult<FacilityOperationsReport>> AsAsync(DefaultHttpContext http, Guid? facility = null)
    {
        var db = NewDb();
        var current = AuthTestContext.RegistrarService(db, http);
        var controller = new OperationsController(
            new FacilityOperationsService(db, new FixedClock(Now)), current,
            new WorkScope(new ReviewQueueScope(new CountyScopeResolver(new CountyLookup(db)), current)))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return await controller.Facilities(facility);
    }

    private static IReadOnlyList<string> Names(ActionResult<FacilityOperationsReport> result)
        => ((FacilityOperationsReport)((OkObjectResult)result.Result!).Value!).Facilities.Select(row => row.Name).ToList();

    [Fact]
    public async Task FacilityStaffSeeTheirOwnFacilityOnly()
    {
        Assert.Equal(["Terekeka Village Health Post"], Names(await AsAsync(AuthTestContext.HttpContextFor())));
        Assert.Equal(StatusCodes.Status403Forbidden,
            ((ObjectResult)(await AsAsync(AuthTestContext.HttpContextFor(), Tali)).Result!).StatusCode);
    }

    [Fact]
    public async Task AnOfficerSeesTheirCounty_AndTheMinistryTheCountry()
    {
        var county = Names(await AsAsync(AuthTestContext.HttpContextFor(OfficerSubject, null, NcbrsRoles.DistrictOfficer)));
        var country = Names(await AsAsync(AuthTestContext.HttpContextFor(MinistrySubject, null, NcbrsRoles.MinistryAdmin)));

        Assert.Equal(3, county.Count);
        Assert.DoesNotContain("Juba Teaching Hospital", county);
        Assert.Equal(4, country.Count);
    }
}
