using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Certificates awaiting collection (plan §17 item 32). Issuing is not
/// delivering: a birth registered offline leaves the family with a slip, and
/// the certificate is printed later where there is signal. These pin that the
/// list holds exactly the births whose certificate has not reached the family,
/// in the same terms as the issuing rules; that a handover takes one off; and
/// that each caller sees only what they act for.
/// </summary>
public class CertificateCollectionTests : IDisposable
{
    private static readonly Guid Terekeka = Guid.Parse("0199a1b2-0001-7000-8000-00000000c001");
    private static readonly Guid Juba = Guid.Parse("0199a1b2-0002-7000-8000-00000000c002");
    private static readonly Guid Tali = Guid.Parse("0199a1b2-0003-7000-8000-00000000c003");
    private static readonly Guid Registrar = Guid.Parse("0199a1b2-1001-7000-8000-00000000c001");
    private static readonly Guid Officer = Guid.Parse("0199a1b2-1002-7000-8000-00000000c002");
    private static readonly Guid Ministry = Guid.Parse("0199a1b2-1003-7000-8000-00000000c003");

    private const string OfficerSubject = "officer-terekeka";
    private const string MinistrySubject = "ministry";

    private readonly TestDatabase _database = TestDatabase.Create();
    private readonly CertificateSigner _signer = new(
        Options.Create(new CertificateSigningOptions { AllowEphemeralDevelopmentKey = true }),
        new DevelopmentEnvironment(),
        NullLogger<CertificateSigner>.Instance);

    private static readonly DateTime Start = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private int _order;

    public CertificateCollectionTests()
    {
        using var db = NewDb();
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = Terekeka, Name = "Terekeka Village Health Post", CountyCode = "SS0104" },
            new Facility { FacilityId = Juba, Name = "Juba Teaching Hospital", CountyCode = "SS0101" },
            new Facility { FacilityId = Tali, Name = "Tali Primary Health Care Unit", CountyCode = "SS0104" });

        db.Registrars.AddRange(
            new Registrar
            {
                RegistrarId = Registrar, FacilityId = Terekeka, ExternalSubjectId = AuthTestContext.DefaultSubject,
                DisplayName = "Nurse Lado", CredentialHash = "test",
            },
            new Registrar
            {
                RegistrarId = Officer, FacilityId = Terekeka, ExternalSubjectId = OfficerSubject,
                DisplayName = "Officer Kenyi", CredentialHash = "test", Role = RegistrarRole.DistrictOfficer,
            },
            new Registrar
            {
                RegistrarId = Ministry, FacilityId = Juba, ExternalSubjectId = MinistrySubject,
                DisplayName = "Ministry", CredentialHash = "test", Role = RegistrarRole.MinistryAdmin,
            });

        // On the list, one per stage.
        var ready = Record("100001", Terekeka);
        var printed = Record("100002", Terekeka);
        printed.Certificates.Add(Certificate());
        var withdrawn = Record("100003", Terekeka);
        withdrawn.Certificates.Add(Certificate(withdrawnAt: Start.AddDays(3)));
        var provisional = Record("PROV-TAB-01-000001", Terekeka);
        provisional.ProvisionalIdentifier = provisional.Brn;
        var late = Record("100004", Terekeka);
        late.LateRegistration = Late(LateRegistrationStatus.PendingApproval);
        var verified = Record("100005", Terekeka);
        verified.LateRegistration = Late(LateRegistrationStatus.Approved);

        // Off the list.
        var collected = Record("100010", Terekeka);
        collected.Certificates.Add(Certificate(handedOverAt: Start.AddDays(2)));
        var stillbirth = Record("100011", Terekeka, VitalEventType.FetalDeath);
        var annulled = Record("100012", Terekeka);
        annulled.AnnulledAtUtc = Start.AddDays(1);
        var refused = Record("100013", Terekeka);
        refused.LateRegistration = Late(LateRegistrationStatus.Rejected);
        var superseded = Record("100014", Terekeka);
        superseded.SupersededByBirthRecordId = ready.BirthRecordId;

        // Another facility in the same county, and another county.
        var neighbour = Record("300001", Tali);
        var elsewhere = Record("200001", Juba);

        db.BirthRecords.AddRange(ready, printed, withdrawn, provisional, late, verified,
            collected, stillbirth, annulled, refused, superseded, neighbour, elsewhere);
        db.SaveChanges();
    }

    public void Dispose()
    {
        _signer.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private BirthRecord Record(string brn, Guid facility, VitalEventType type = VitalEventType.LiveBirth)
        => new()
        {
            Brn = brn,
            VitalEventType = type,
            ChildPerson = new Person { FullName = $"Child {brn}" },
            FacilityId = facility,
            RegisteredByRegistrarId = Registrar,
            DateOfBirth = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            CreatedAtUtc = Start.AddMinutes(_order++),
        };

    private static Certificate Certificate(DateTime? withdrawnAt = null, DateTime? handedOverAt = null)
        => new()
        {
            IssueDateUtc = Start.AddDays(1),
            SignatureHash = "sig",
            QrPayload = "qr",
            WithdrawnAtUtc = withdrawnAt,
            WithdrawnReason = withdrawnAt is null ? null : "Amended",
            HandedOverAtUtc = handedOverAt,
            HandedOverTo = handedOverAt is null ? null : CertificateRecipient.Mother,
        };

    private static LateRegistration Late(LateRegistrationStatus status)
        => new()
        {
            DaysLate = 200,
            WindowDaysAtFiling = 90,
            EvidenceType = LateRegistrationEvidenceType.HealthFacilityRecord,
            DeclarantName = "Mary Ayen",
            DeclarantRelationship = "Mother",
            Status = status,
            SubmittedByRegistrarId = Registrar,
        };

    private static async Task<Page<CertificateQueueItem>> ListAsync(
        NcbrsDbContext db, Guid? facility = null, string? county = null, CertificateQueueStage? stage = null)
        => await new CertificateQueueService(db).ListAsync(facility, county, stage, new PageRequest());

    // --- what is on the list ---------------------------------------------------------------------

    [Fact]
    public async Task TheListHoldsEveryBirthWhoseCertificateHasNotReachedTheFamily_InItsStage()
    {
        await using var db = NewDb();

        var page = await ListAsync(db, county: "SS0104");

        Assert.Equal(
            new (string, CertificateQueueStage)[]
            {
                ("100001", CertificateQueueStage.ToIssue),
                ("100002", CertificateQueueStage.ToHandOver),
                ("100003", CertificateQueueStage.ToIssue),           // withdrawn: the family's paper no longer stands
                ("PROV-TAB-01-000001", CertificateQueueStage.AwaitingNumber),
                ("100004", CertificateQueueStage.AwaitingVerification),
                ("100005", CertificateQueueStage.ToIssue),           // verified late: nothing in the way now
                ("300001", CertificateQueueStage.ToIssue),           // the neighbouring facility, same county
            },
            page.Items.Select(item => (item.Brn, item.Stage)));
        Assert.Equal(7, page.Total);
    }

    [Fact]
    public async Task OnlyThePrintedOneCarriesWhenItWasIssued()
    {
        await using var db = NewDb();

        var page = await ListAsync(db, county: "SS0104");

        Assert.Equal(["100002"], page.Items.Where(item => item.CertificateIssuedAtUtc is not null).Select(item => item.Brn));
    }

    [Theory]
    [InlineData(CertificateQueueStage.ToIssue, new[] { "100001", "100003", "100005", "300001" })]
    [InlineData(CertificateQueueStage.ToHandOver, new[] { "100002" })]
    [InlineData(CertificateQueueStage.AwaitingNumber, new[] { "PROV-TAB-01-000001" })]
    [InlineData(CertificateQueueStage.AwaitingVerification, new[] { "100004" })]
    public async Task OneStageAtATime(CertificateQueueStage stage, string[] brns)
    {
        await using var db = NewDb();

        var page = await ListAsync(db, county: "SS0104", stage: stage);

        Assert.Equal(brns, page.Items.Select(item => item.Brn));
        Assert.All(page.Items, item => Assert.Equal(stage, item.Stage));
    }

    /// <summary>A queue worked oldest-first must not skip anyone between pages.</summary>
    [Fact]
    public async Task PagesFollowEachOtherWithoutGapsOrRepeats()
    {
        await using var db = NewDb();
        var service = new CertificateQueueService(db);

        var first = await service.ListAsync(null, "SS0104", null, new PageRequest { Limit = 4 });
        var second = await service.ListAsync(null, "SS0104", null, new PageRequest { Limit = 4, After = first.NextCursor });

        Assert.NotNull(first.NextCursor);
        Assert.Null(second.NextCursor);
        Assert.Equal(
            (await ListAsync(db, county: "SS0104")).Items.Select(item => item.Brn),
            first.Items.Concat(second.Items).Select(item => item.Brn));
    }

    // --- handing over --------------------------------------------------------------------------

    private async Task<HandoverOutcome> HandOverAsync(string brn, CertificateRecipient to = CertificateRecipient.Mother)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = await db.Registrars.SingleAsync(r => r.RegistrarId == Registrar);

        return await new CertificateService(db, _signer, current, new CountyLookup(db))
            .HandOverAsync(brn, to, registrar, "ncbrs-web", Guid.CreateVersion7());
    }

    [Fact]
    public async Task AHandoverTakesTheBirthOffTheList_AndIsAudited()
    {
        var outcome = await HandOverAsync("100002", CertificateRecipient.Father);

        Assert.Equal(CertificateResult.Issued, outcome.Result);
        Assert.Equal(CertificateRecipient.Father, outcome.Response!.HandedOverTo);

        await using var db = NewDb();
        Assert.DoesNotContain("100002", (await ListAsync(db, county: "SS0104")).Items.Select(item => item.Brn));
        var certificate = await db.Certificates.SingleAsync(c => c.BirthRecord!.Brn == "100002");
        Assert.Equal((CertificateRecipient.Father, Registrar), (certificate.HandedOverTo!.Value, certificate.HandedOverByRegistrarId!.Value));
        var audit = await db.AuditLogs.SingleAsync(row => row.EntityId == "100002");
        Assert.Equal("CertificateHandedOver:Father", audit.Action);
        Assert.Equal("SS0104", audit.CountyCode);
    }

    [Fact]
    public async Task AHandoverIsRecordedOnce()
    {
        await HandOverAsync("100002");

        var again = await HandOverAsync("100002", CertificateRecipient.Guardian);

        Assert.Equal(CertificateResult.AlreadyHandedOver, again.Result);
        await using var db = NewDb();
        Assert.Equal(CertificateRecipient.Mother, (await db.Certificates.SingleAsync(c => c.BirthRecord!.Brn == "100002")).HandedOverTo);
    }

    [Theory]
    [InlineData("100001")]   // never issued
    [InlineData("100003")]   // issued, then withdrawn
    public async Task NothingCanBeHandedOverWithoutAStandingCertificate(string brn)
        => Assert.Equal(CertificateResult.NoCertificateToHandOver, (await HandOverAsync(brn)).Result);

    [Fact]
    public async Task AnAnnulledRecordHasNothingToHandOver()
        => Assert.Equal(CertificateResult.RecordAnnulled, (await HandOverAsync("100012")).Result);

    [Fact]
    public async Task AnotherFacilitysCertificateIsNotTheirsToHandOver()
        => Assert.Equal(CertificateResult.NotPermitted, (await HandOverAsync("200001")).Result);

    /// <summary>The enum's first value is Mother; a request that left the recipient out must not record her.</summary>
    [Fact]
    public void WhoReceivedItMustBeSaid()
    {
        var validator = new HandOverCertificateRequestValidator();

        Assert.False(validator.Validate(new HandOverCertificateRequest { DeviceId = "ncbrs-web" }).IsValid);
        Assert.True(validator.Validate(new HandOverCertificateRequest { DeviceId = "ncbrs-web", ReceivedBy = CertificateRecipient.Mother }).IsValid);
    }

    // --- who sees what -------------------------------------------------------------------------

    private async Task<ActionResult<Page<CertificateQueueItem>>> AsAsync(DefaultHttpContext http, Guid? facility = null)
    {
        var db = NewDb();
        var current = AuthTestContext.RegistrarService(db, http);
        var controller = new CertificateQueueController(
            new CertificateQueueService(db), current, new WorkScope(new ReviewQueueScope(new CountyScopeResolver(new CountyLookup(db)), current)))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return await controller.List(facility);
    }

    private static IReadOnlyList<string> Brns(ActionResult<Page<CertificateQueueItem>> result)
        => ((Page<CertificateQueueItem>)((OkObjectResult)result.Result!).Value!).Items.Select(item => item.Brn).ToList();

    private static int? Status(ActionResult<Page<CertificateQueueItem>> result) => (result.Result as ObjectResult)?.StatusCode;

    [Fact]
    public async Task FacilityStaffSeeTheirOwnFacility_AndAreRefusedAnother()
    {
        var own = await AsAsync(AuthTestContext.HttpContextFor());
        var other = await AsAsync(AuthTestContext.HttpContextFor(), Juba);

        Assert.Equal(6, Brns(own).Count);
        Assert.DoesNotContain("300001", Brns(own));   // a neighbouring facility in the same county
        Assert.Equal(StatusCodes.Status403Forbidden, Status(other));
    }

    [Fact]
    public async Task ADistrictOfficerSeesTheirCounty_AndIsRefusedAnothersFacility()
    {
        var county = await AsAsync(AuthTestContext.HttpContextFor(OfficerSubject, null, NcbrsRoles.DistrictOfficer));
        var other = await AsAsync(AuthTestContext.HttpContextFor(OfficerSubject, null, NcbrsRoles.DistrictOfficer), Juba);

        Assert.Equal(7, Brns(county).Count);
        Assert.Contains("300001", Brns(county));   // the whole county, not just the officer's own facility
        Assert.Equal(StatusCodes.Status403Forbidden, Status(other));
    }

    [Fact]
    public async Task TheMinistrySeesTheCountry()
    {
        var national = await AsAsync(AuthTestContext.HttpContextFor(MinistrySubject, null, NcbrsRoles.MinistryAdmin));

        Assert.Equal(8, Brns(national).Count);
        Assert.Contains("200001", Brns(national));
    }
}
