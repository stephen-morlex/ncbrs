using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers births registered after the statutory window (draft 4.1, 5.3).
///
/// Two things carry the weight here. The window is measured to the moment a
/// birth was captured on a device, not the moment it reached the server --
/// otherwise every registration from a post that spent three weeks offline
/// would be wrongly routed into an evidence-verification process. And the
/// verification has to actually withhold something, or backdating costs a
/// forger nothing; what it withholds is the certificate.
/// </summary>
public class LateRegistrationTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid ReviewerId = Guid.Parse("0199a1b2-1002-7000-8000-000000000002");
    private const string ReviewerSubject = "22222222-2222-4222-8222-222222222222";

    private const int WindowDays = 90;

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly CertificateSigner _signer;

    public LateRegistrationTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        _signer = new CertificateSigner(
            Options.Create(new CertificateSigningOptions { AllowEphemeralDevelopmentKey = true }),
            new DevelopmentEnvironment(),
            NullLogger<CertificateSigner>.Instance);

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

        db.Registrars.AddRange(
            new Registrar
            {
                RegistrarId = RegistrarId,
                FacilityId = FacilityId,
                ExternalSubjectId = AuthTestContext.DefaultSubject,
                DisplayName = "Nurse A. Lado",
                CredentialHash = "test"
            },
            new Registrar
            {
                RegistrarId = ReviewerId,
                FacilityId = FacilityId,
                ExternalSubjectId = ReviewerSubject,
                DisplayName = "District Officer M. Kenyi",
                Role = RegistrarRole.DistrictOfficer
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

    private static BirthRegistrationService Registrations(
        NcbrsDbContext db, CurrentRegistrarService current, int windowDays = WindowDays)
        => new(db, new NoOpEventPublisher(), current,
            new DuplicateDetectionService(db, new DuplicateMatcher(),
                new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance,
                new CountyLookup(db)),
            new CountyLookup(db),
            Options.Create(new StatutoryRegistrationOptions { WindowDays = windowDays }));

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

    private static LateRegistrationDetails Evidence(
        LateRegistrationEvidenceType type = LateRegistrationEvidenceType.BirthAttendantAttestation)
        => new()
        {
            EvidenceType = type,
            EvidenceReference = "TBA attestation 44/2026",
            DeclarantName = "Nyandeng Deng",
            DeclarantRelationship = "mother"
        };

    private async Task<RegistrationResult> RegisterAsync(RegisterBirthRequest request, int windowDays = WindowDays)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        return await Registrations(db, current, windowDays)
            .RegisterAsync(request, registrar, Guid.CreateVersion7());
    }

    private async Task<LateRegistrationReviewOutcome> ReviewAsync(
        Guid lateRegistrationId, bool approve, Guid? reviewerId = null)
    {
        await using var db = NewDb();
        var subject = (reviewerId ?? ReviewerId) == ReviewerId
            ? ReviewerSubject
            : AuthTestContext.DefaultSubject;

        var http = AuthTestContext.HttpContextFor(subject, NcbrsRoles.DistrictOfficer);
        var current = AuthTestContext.RegistrarService(db, http);
        var reviewer = db.Registrars.Single(r => r.RegistrarId == (reviewerId ?? ReviewerId));

        return await new LateRegistrationService(db, current, new CountyLookup(db))
            .ReviewAsync(lateRegistrationId, approve, "Attestation checked against the TBA register.",
                reviewer, Guid.CreateVersion7());
    }

    private async Task<CertificateOutcome> IssueCertificateAsync(string brn)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        return await new CertificateService(db, _signer, current, new CountyLookup(db))
            .IssueAsync(brn, registrar, "TABLET-07", Guid.CreateVersion7());
    }

    // --- the window -------------------------------------------------------

    [Fact]
    public async Task ABirthInsideTheWindow_NeedsNoEvidence()
    {
        var result = await RegisterAsync(Request("100001", bornDaysAgo: 10));

        Assert.True(result.Succeeded);
        Assert.Null(result.LateRegistration);

        await using var db = NewDb();
        Assert.Empty(await db.LateRegistrations.ToListAsync());
    }

    /// <summary>
    /// The boundary is the window itself: a birth registered on the last day
    /// of it is on time.
    /// </summary>
    [Theory]
    [InlineData(WindowDays - 1, false)]
    [InlineData(WindowDays, false)]
    [InlineData(WindowDays + 1, true)]
    public async Task TheWindowBoundary_IsInclusive(int bornDaysAgo, bool expectedLate)
    {
        var result = await RegisterAsync(Request("100001", bornDaysAgo,
            late: expectedLate ? Evidence() : null));

        Assert.True(result.Succeeded);
        Assert.Equal(expectedLate, result.LateRegistration is not null);
    }

    [Fact]
    public async Task ABirthOutsideTheWindowWithoutEvidence_IsRefusedWithWhatIsNeeded()
    {
        var result = await RegisterAsync(Request("100001", bornDaysAgo: 200));

        Assert.Equal(RegistrationOutcome.LateRegistrationEvidenceRequired, result.Outcome);
        Assert.Contains("200 days", result.Detail);
        Assert.Contains("90-day statutory window", result.Detail);

        await using var db = NewDb();
        Assert.Empty(await db.BirthRecords.ToListAsync());
    }

    /// <summary>
    /// Evidence on an on-time birth means the device and the registry
    /// disagree about the date. Ignoring it would hide that.
    /// </summary>
    [Fact]
    public async Task EvidenceOnAnOnTimeBirth_IsRefused()
    {
        var result = await RegisterAsync(Request("100001", bornDaysAgo: 10, late: Evidence()));

        Assert.Equal(RegistrationOutcome.LateRegistrationEvidenceRequired, result.Outcome);
        Assert.Contains("does not apply", result.Detail);
    }

    [Fact]
    public async Task TheWindowIsConfigurable_BecauseTheActSetsIt()
    {
        // The same birth is late under a 30-day Act and on time under 90.
        Assert.Equal(RegistrationOutcome.LateRegistrationEvidenceRequired,
            (await RegisterAsync(Request("100001", bornDaysAgo: 45), windowDays: 30)).Outcome);

        Assert.True((await RegisterAsync(Request("100002", bornDaysAgo: 45), windowDays: 90)).Succeeded);
    }

    // --- the offline case, which is the whole point ------------------------

    /// <summary>
    /// The case that would break the offline tier if it were measured wrong.
    /// A village post captures a birth two days after it happens, then syncs
    /// five months later. That is an on-time registration delivered late, not
    /// a late registration.
    /// </summary>
    [Fact]
    public async Task ABirthCapturedOnTimeButSyncedMuchLater_IsNotLate()
    {
        var result = await RegisterAsync(Request(
            "100001",
            bornDaysAgo: 150,
            capturedAt: DateTime.UtcNow.Date.AddDays(-148)));

        Assert.True(result.Succeeded);
        Assert.Null(result.LateRegistration);
    }

    /// <summary>
    /// The mirror case: a device that was online all along cannot claim a
    /// capture time it never had. Without a bound, backdating the capture
    /// would sidestep the whole process.
    /// </summary>
    [Fact]
    public async Task ACaptureTimeBeforeTheBirth_IsRefused()
    {
        var result = await RegisterAsync(Request(
            "100001",
            bornDaysAgo: 100,
            capturedAt: DateTime.UtcNow.Date.AddDays(-120)));

        Assert.Equal(RegistrationOutcome.ImplausibleCaptureTime, result.Outcome);
    }

    [Fact]
    public async Task ACaptureTimeFarInTheFuture_IsRefused()
    {
        var result = await RegisterAsync(Request(
            "100001",
            bornDaysAgo: 10,
            capturedAt: DateTime.UtcNow.AddDays(3)));

        Assert.Equal(RegistrationOutcome.ImplausibleCaptureTime, result.Outcome);
    }

    /// <summary>
    /// A village clock drifting a few hours must not refuse a registration.
    /// </summary>
    [Fact]
    public async Task AModestClockDrift_IsTolerated()
    {
        var result = await RegisterAsync(Request(
            "100001",
            bornDaysAgo: 10,
            capturedAt: DateTime.UtcNow.AddHours(4)));

        Assert.True(result.Succeeded);
    }

    // --- what gets recorded ------------------------------------------------

    [Fact]
    public async Task ALateRegistration_RecordsItsEvidenceAndDeclarant()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200,
            late: Evidence(LateRegistrationEvidenceType.SwornAffidavit)));

        await using var db = NewDb();
        var late = await db.LateRegistrations.SingleAsync();

        Assert.Equal(LateRegistrationEvidenceType.SwornAffidavit, late.EvidenceType);
        Assert.Equal("Nyandeng Deng", late.DeclarantName);
        Assert.Equal("mother", late.DeclarantRelationship);
        Assert.Equal(200, late.DaysLate);
        Assert.Equal(RegistrarId, late.SubmittedByRegistrarId);
        Assert.Equal(LateRegistrationStatus.PendingApproval, late.Status);
    }

    /// <summary>
    /// The window is set in law and may change. A record has to stay
    /// explicable under the rule that applied when it was filed.
    /// </summary>
    [Fact]
    public async Task TheWindowInForce_IsStoredWithTheRecord()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 60, late: Evidence()), windowDays: 30);

        await using var db = NewDb();
        Assert.Equal(30, (await db.LateRegistrations.SingleAsync()).WindowDaysAtFiling);
    }

    /// <summary>
    /// The registration itself always succeeds. A child registered late is
    /// still a child who exists, and refusing the record would leave them
    /// with no legal identity at all.
    /// </summary>
    [Fact]
    public async Task ALateRegistration_StillCreatesTheRecord()
    {
        var result = await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        Assert.True(result.Succeeded);

        await using var db = NewDb();
        Assert.Equal("100001", (await db.BirthRecords.SingleAsync()).Brn);
    }

    [Fact]
    public async Task FilingALateRegistration_IsAudited()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(log => log.Action == "LateRegistrationFiled");

        Assert.Equal("100001", audit.EntityId);
        Assert.Equal(RegistrarId, audit.UserId);
    }

    // --- what verification withholds ---------------------------------------

    /// <summary>
    /// The teeth. Without this the verification step is advisory and
    /// backdating costs a forger nothing.
    /// </summary>
    [Fact]
    public async Task NoCertificateIsIssued_WhileVerificationIsPending()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        var result = await IssueCertificateAsync("100001");

        Assert.Equal(CertificateResult.LateRegistrationNotVerified, result.Result);
        Assert.Contains("200 days", result.Detail);

        await using var db = NewDb();
        Assert.Empty(await db.Certificates.ToListAsync());
    }

    [Fact]
    public async Task OnceVerified_TheCertificateIsIssued()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        Guid lateId;
        await using (var db = NewDb())
        {
            lateId = (await db.LateRegistrations.SingleAsync()).LateRegistrationId;
        }

        Assert.True((await ReviewAsync(lateId, approve: true)).Succeeded);

        var result = await IssueCertificateAsync("100001");

        Assert.True(result.Succeeded);
        Assert.NotNull(_signer.Verify(result.Response!.QrPayload));
    }

    [Fact]
    public async Task ARefusedLateRegistration_NeverYieldsACertificate()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        Guid lateId;
        await using (var db = NewDb())
        {
            lateId = (await db.LateRegistrations.SingleAsync()).LateRegistrationId;
        }

        await ReviewAsync(lateId, approve: false);

        var result = await IssueCertificateAsync("100001");

        Assert.Equal(CertificateResult.LateRegistrationNotVerified, result.Result);
        Assert.Contains("refused", result.Detail);
    }

    /// <summary>
    /// An on-time birth must not be dragged through any of this.
    /// </summary>
    [Fact]
    public async Task AnOnTimeBirth_IsCertifiedImmediately()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 10));

        Assert.True((await IssueCertificateAsync("100001")).Succeeded);
    }

    // --- verification -------------------------------------------------------

    [Fact]
    public async Task TheFilingRegistrar_CannotVerifyTheirOwnClaim()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        Guid lateId;
        await using (var db = NewDb())
        {
            lateId = (await db.LateRegistrations.SingleAsync()).LateRegistrationId;
        }

        var result = await ReviewAsync(lateId, approve: true, reviewerId: RegistrarId);

        Assert.Equal(LateRegistrationReviewResult.NotPermitted, result.Result);
        Assert.Contains("filed it", result.Detail);
    }

    [Fact]
    public async Task VerifyingTwice_IsRefused()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        Guid lateId;
        await using (var db = NewDb())
        {
            lateId = (await db.LateRegistrations.SingleAsync()).LateRegistrationId;
        }

        await ReviewAsync(lateId, approve: true);

        Assert.Equal(LateRegistrationReviewResult.AlreadyReviewed,
            (await ReviewAsync(lateId, approve: true)).Result);
    }

    [Fact]
    public async Task AnUnknownLateRegistration_IsNotFound()
        => Assert.Equal(LateRegistrationReviewResult.NotFound,
            (await ReviewAsync(Guid.CreateVersion7(), approve: true)).Result);

    /// <summary>
    /// A refused claim is kept, so the same one cannot simply be refiled as
    /// though it had never been seen.
    /// </summary>
    [Fact]
    public async Task ARefusedClaim_IsRetainedWithItsReason()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        Guid lateId;
        await using (var db = NewDb())
        {
            lateId = (await db.LateRegistrations.SingleAsync()).LateRegistrationId;
        }

        await ReviewAsync(lateId, approve: false);

        await using var verify = NewDb();
        var late = await verify.LateRegistrations.SingleAsync();

        Assert.Equal(LateRegistrationStatus.Rejected, late.Status);
        Assert.Equal(ReviewerId, late.ReviewedByRegistrarId);
        Assert.Contains("TBA register", late.ReviewNote);
    }

    // --- the queue ----------------------------------------------------------

    /// <summary>
    /// The case keyset paging exists for (W7).
    ///
    /// A reviewer pages through the queue while working it. With offset
    /// paging, verifying the first page's entries shifts everything below
    /// them up by that many rows, and asking for "page 2" steps straight past
    /// the ones that moved — so entries are never seen by a reviewer who
    /// believes the queue is done. A cursor is a position in the ordering,
    /// so removing everything before it changes nothing.
    /// </summary>
    [Fact]
    public async Task WorkingTheQueueWhilePagingSkipsNothing()
    {
        for (var index = 1; index <= 6; index++)
        {
            await RegisterAsync(Request($"10000{index}", bornDaysAgo: 100 + index, late: Evidence()));
        }

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            await using var db = NewDb();
            var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);

            var page = await new LateRegistrationService(db, AuthTestContext.RegistrarService(db, http), new CountyLookup(db))
                .PendingAsync(null, new PageRequest { Limit = 2, After = cursor });

            seen.AddRange(page.Items.Select(entry => entry.Brn));
            cursor = page.NextCursor;

            // The reviewer acts on what they were just shown, which removes it
            // from the queue -- exactly what breaks offset paging.
            foreach (var entry in page.Items)
            {
                await ReviewAsync(entry.LateRegistrationId, approve: true);
            }
        }
        while (cursor is not null);

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task TheQueueIsPagedAndReportsTheWholeBacklog()
    {
        for (var index = 1; index <= 5; index++)
        {
            await RegisterAsync(Request($"10000{index}", bornDaysAgo: 100 + index, late: Evidence()));
        }

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);
        var service = new LateRegistrationService(db, AuthTestContext.RegistrarService(db, http), new CountyLookup(db));

        var first = await service.PendingAsync(null, new PageRequest { Limit = 2 });

        Assert.Equal(2, first.Items.Count);

        // Total is the backlog, not the page -- it is what tells an officer
        // how much work is waiting.
        Assert.Equal(5, first.Total);
        Assert.NotNull(first.NextCursor);

        var second = await service.PendingAsync(null, new PageRequest { Limit = 2, After = first.NextCursor });

        Assert.Equal(2, second.Items.Count);
        Assert.Empty(second.Items.Select(entry => entry.Brn).Intersect(first.Items.Select(entry => entry.Brn)));
    }

    [Fact]
    public async Task PendingClaims_AppearInTheQueueOldestFirst()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));
        await RegisterAsync(Request("100002", bornDaysAgo: 400, late: Evidence()));

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);
        var queue = await new LateRegistrationService(db, AuthTestContext.RegistrarService(db, http), new CountyLookup(db))
            .PendingAsync(null, new PageRequest());

        Assert.Equal(2, queue.Total);
        Assert.Equal(2, queue.Items.Count);
        Assert.Null(queue.NextCursor);
        Assert.Equal("100001", queue.Items[0].Brn);
        Assert.Equal("Nurse A. Lado", queue.Items[0].SubmittedByRegistrarName);
        Assert.Equal("Terekeka Village Health Post", queue.Items[0].FacilityName);
        Assert.Equal(400, queue.Items[1].DaysLate);
    }

    [Fact]
    public async Task AVerifiedClaim_LeavesTheQueue()
    {
        await RegisterAsync(Request("100001", bornDaysAgo: 200, late: Evidence()));

        Guid lateId;
        await using (var db = NewDb())
        {
            lateId = (await db.LateRegistrations.SingleAsync()).LateRegistrationId;
        }

        await ReviewAsync(lateId, approve: true);

        await using var verify = NewDb();
        var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);
        var queue = await new LateRegistrationService(verify, AuthTestContext.RegistrarService(verify, http),
            new CountyLookup(verify))
            .PendingAsync(null, new PageRequest());

        Assert.Empty(queue.Items);
    }
}
