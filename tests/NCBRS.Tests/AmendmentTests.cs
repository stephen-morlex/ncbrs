using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers corrections to a registered birth (draft Section 5.3) and the
/// ncbrs.birth-records.amended stream.
///
/// The load-bearing claims: a previous value survives the correction, a
/// certificate contradicted by one stops being valid, a record already ruled
/// a duplicate is not the one corrected -- and the two-track rule, where a
/// clerical fix takes effect at once but a change to how the register
/// identifies a person waits for someone other than its author.
/// </summary>
public class AmendmentServiceTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid OtherFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid ReviewerId = Guid.Parse("0199a1b2-1002-7000-8000-000000000002");
    private static readonly Guid SurvivorId = Guid.Parse("0199a1b2-2001-7000-8000-000000000001");

    private const string ReviewerSubject = "22222222-2222-4222-8222-222222222222";

    private const string Brn = "100001";
    private const string OtherFacilityBrn = "200001";
    private const string SupersededBrn = "100009";
    private const string Reason = "Name misspelled on the original form.";

    private static readonly DateTime BornAt = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly CertificateSigner _signer;

    public AmendmentServiceTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        _signer = new CertificateSigner(
            Options.Create(new CertificateSigningOptions { AllowEphemeralDevelopmentKey = true }),
            new DevelopmentEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CertificateSigner>.Instance);

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = FacilityId, Name = "Kabwe Village Health Post", DistrictId = "D-CENTRAL-07" },
            new Facility { FacilityId = OtherFacilityId, Name = "Lusaka Central", DistrictId = "D-LUSAKA-01" });

        db.Registrars.AddRange(
            new Registrar
            {
                RegistrarId = RegistrarId,
                FacilityId = FacilityId,
                ExternalSubjectId = AuthTestContext.DefaultSubject,
                DisplayName = "Nurse A. Banda",
                CredentialHash = "test"
            },
            new Registrar
            {
                RegistrarId = ReviewerId,
                FacilityId = FacilityId,
                ExternalSubjectId = ReviewerSubject,
                DisplayName = "District Officer M. Tembo",
                Role = RegistrarRole.DistrictOfficer
            });

        var superseded = Record(SupersededBrn, FacilityId);
        superseded.SupersededByBirthRecordId = SurvivorId;

        db.BirthRecords.AddRange(
            Record(Brn, FacilityId),
            Record(OtherFacilityBrn, OtherFacilityId),
            superseded);

        db.SaveChanges();
    }

    private static BirthRecord Record(string brn, Guid facilityId)
        => new()
        {
            Brn = brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Chipo Mwale" },
            FacilityId = facilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = BornAt,
            Sex = Sex.Female,
            BirthWeightGrams = 3200,
            Plurality = BirthPlurality.Singleton,
            Status = RecordStatus.Confirmed
        };

    public void Dispose()
    {
        _signer.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private static AmendmentService Service(NcbrsDbContext db, CurrentRegistrarService current, NoOpEventPublisher publisher)
        => new(db, publisher, new CertificateRevocationRecorder(db), current, new DistrictLookup(db));

    private async Task<(AmendmentOutcome Outcome, NoOpEventPublisher Publisher)> AmendAsync(
        AmendBirthRecordRequest request,
        string brn = Brn,
        params string[] roles)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: roles);
        var current = AuthTestContext.RegistrarService(db, http);
        var publisher = new NoOpEventPublisher();
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var outcome = await Service(db, current, publisher)
            .AmendAsync(brn, request, registrar, Guid.CreateVersion7());

        return (outcome, publisher);
    }

    private async Task<(AmendmentReviewOutcome Outcome, NoOpEventPublisher Publisher)> ReviewAsync(
        Guid requestId,
        bool approve,
        string? note = "Checked against the hospital register.",
        Guid? reviewerId = null)
    {
        await using var db = NewDb();
        var subject = reviewerId is null || reviewerId == ReviewerId
            ? ReviewerSubject
            : AuthTestContext.DefaultSubject;

        var http = AuthTestContext.HttpContextFor(subject, NcbrsRoles.DistrictOfficer);
        var current = AuthTestContext.RegistrarService(db, http);
        var publisher = new NoOpEventPublisher();
        var reviewer = db.Registrars.Single(r => r.RegistrarId == (reviewerId ?? ReviewerId));

        var outcome = await Service(db, current, publisher)
            .ReviewAsync(requestId, approve, reviewer, note, Guid.CreateVersion7());

        return (outcome, publisher);
    }

    private static AmendBirthRecordRequest Amendment(
        string? childFullName = null,
        int? birthWeightGrams = null,
        string? motherFullName = null,
        DateTime? dateOfBirth = null,
        Sex? sex = null)
        => new()
        {
            ChildFullName = childFullName,
            BirthWeightGrams = birthWeightGrams,
            MotherFullName = motherFullName,
            DateOfBirth = dateOfBirth,
            Sex = sex,
            Reason = Reason,
            DeviceId = "TABLET-07"
        };

    // --- the immediate track ----------------------------------------------

    /// <summary>
    /// Birth weight is clerical. Holding it behind a district officer would
    /// help nobody and would teach staff that corrections are not worth
    /// making.
    /// </summary>
    [Fact]
    public async Task AClericalCorrection_TakesEffectAtOnce()
    {
        var (outcome, _) = await AmendAsync(Amendment(birthWeightGrams: 3250));

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.EverythingPending);
        Assert.Single(outcome.Response!.Applied);
        Assert.Empty(outcome.Response.PendingApproval);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync(r => r.Brn == Brn);

        Assert.Equal(3250, record.BirthWeightGrams);
        Assert.Equal(RecordStatus.Amended, record.Status);
    }

    [Fact]
    public async Task AClericalCorrection_IsPublishedImmediately()
    {
        var (_, publisher) = await AmendAsync(Amendment(birthWeightGrams: 3250));

        var published = Assert.Single(publisher.Amendments);
        Assert.Equal(nameof(AmendBirthRecordRequest.BirthWeightGrams),
            Assert.Single(published.Changes).Field);
    }

    /// <summary>
    /// A parent often goes unrecorded at first registration and is named
    /// later, so approval has to create the Person rather than fail.
    /// </summary>
    [Fact]
    public async Task NamingAParentNotRecordedBefore_AddsThemOnApproval()
    {
        var (submitted, _) = await AmendAsync(Amendment(motherFullName: "Grace Mwale"));
        await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.MotherPerson).SingleAsync(r => r.Brn == Brn);

        Assert.Equal("Grace Mwale", record.MotherPerson!.FullName);
        Assert.Null((await db.BirthRecordAmendments.SingleAsync()).PreviousValue);
    }

    /// <summary>
    /// Filiation is not clerical. Nothing on the certificate changes when a
    /// father's name is corrected, but who the child belongs to does -- it is
    /// the field a disputed paternity would be rewritten through, and the one
    /// an inheritance claim later turns on.
    /// </summary>
    [Theory]
    [InlineData("mother")]
    [InlineData("father")]
    public async Task CorrectingAParentsName_WaitsForApproval(string parent)
    {
        var request = parent == "mother"
            ? Amendment(motherFullName: "Grace Mwale")
            : Amendment() with { FatherFullName = "Joseph Mwale" };

        var (outcome, publisher) = await AmendAsync(request);

        Assert.True(outcome.EverythingPending);
        Assert.Empty(publisher.Amendments);

        await using var db = NewDb();
        var record = await db.BirthRecords
            .Include(r => r.MotherPerson).Include(r => r.FatherPerson)
            .SingleAsync(r => r.Brn == Brn);

        Assert.Null(record.MotherPerson);
        Assert.Null(record.FatherPerson);
    }

    /// <summary>
    /// The immediate track is now exactly the clinical measurements. This
    /// pins that line: everything describing who the record is about waits,
    /// everything describing the birth event itself does not.
    /// </summary>
    [Fact]
    public async Task OnlyClinicalMeasurements_StayOnTheImmediateTrack()
    {
        var (outcome, _) = await AmendAsync(new AmendBirthRecordRequest
        {
            BirthWeightGrams = 3250,
            GestationalAgeWeeks = 38.5m,
            BirthOrder = 1,
            ChildFullName = "Chipo Mwale-Banda",
            MotherFullName = "Grace Mwale",
            FatherFullName = "Joseph Mwale",
            Sex = Sex.Male,
            Reason = Reason,
            DeviceId = "TABLET-07"
        });

        Assert.Equal(
            ["BirthOrder", "BirthWeightGrams", "GestationalAgeWeeks"],
            outcome.Response!.Applied.Select(change => change.Field).Order());

        Assert.Equal(
            ["ChildFullName", "FatherFullName", "MotherFullName", "Sex"],
            outcome.Response.PendingApproval.Select(change => change.Field).Order());
    }

    // --- the approval track -----------------------------------------------

    /// <summary>
    /// The child's name is what the certificate signature covers and what
    /// decides which person the register describes. It waits.
    /// </summary>
    [Fact]
    public async Task ANameCorrection_WaitsForApproval()
    {
        var (outcome, publisher) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.EverythingPending);
        Assert.Empty(outcome.Response!.Applied);
        Assert.Single(outcome.Response.PendingApproval);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync(r => r.Brn == Brn);

        // Unchanged, and nothing downstream has been told otherwise.
        Assert.Equal("Chipo Mwale", record.ChildPerson!.FullName);
        Assert.Equal(RecordStatus.Confirmed, record.Status);
        Assert.Empty(publisher.Amendments);
    }

    [Fact]
    public async Task AnApprovedCorrection_TakesEffect()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        var (review, publisher) = await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        Assert.True(review.Succeeded);
        Assert.Equal(AmendmentStatus.Applied, review.Response!.Status);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync(r => r.Brn == Brn);

        Assert.Equal("Chipo Mwale-Banda", record.ChildPerson!.FullName);
        Assert.Equal(RecordStatus.Amended, record.Status);

        // Only now does anything downstream hear about it.
        Assert.Single(publisher.Amendments);
    }

    [Fact]
    public async Task ARefusedCorrection_LeavesTheRecordAlone()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        var (review, publisher) = await ReviewAsync(
            submitted.Response!.AmendmentRequestId, approve: false, note: "Not supported by the hospital register.");

        Assert.True(review.Succeeded);
        Assert.Equal(AmendmentStatus.Rejected, review.Response!.Status);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync(r => r.Brn == Brn);

        Assert.Equal("Chipo Mwale", record.ChildPerson!.FullName);
        Assert.Equal(RecordStatus.Confirmed, record.Status);
        Assert.Empty(publisher.Amendments);
    }

    /// <summary>
    /// A refused change is history too, and often the part a dispute turns
    /// on -- so it is kept, not deleted.
    /// </summary>
    [Fact]
    public async Task ARefusedCorrection_IsKeptWithItsReason()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: false, note: "Unsupported.");

        await using var db = NewDb();
        var row = await db.BirthRecordAmendments.SingleAsync();

        Assert.Equal(AmendmentStatus.Rejected, row.Status);
        Assert.Equal("Unsupported.", row.ReviewNote);
        Assert.Equal(ReviewerId, row.ReviewedByRegistrarId);
        Assert.Null(row.AppliedAtUtc);
    }

    /// <summary>
    /// The entire point of a second track is a second pair of eyes. An author
    /// approving their own correction is the same pair.
    /// </summary>
    [Fact]
    public async Task TheSubmitter_CannotApproveTheirOwnCorrection()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        var (review, _) = await ReviewAsync(
            submitted.Response!.AmendmentRequestId, approve: true, reviewerId: RegistrarId);

        Assert.Equal(AmendmentReviewResult.NotPermitted, review.Result);

        await using var db = NewDb();
        Assert.Equal(AmendmentStatus.PendingApproval, (await db.BirthRecordAmendments.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReviewingTwice_IsRefused()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        var (second, _) = await ReviewAsync(submitted.Response.AmendmentRequestId, approve: false);

        Assert.Equal(AmendmentReviewResult.AlreadyReviewed, second.Result);
    }

    [Fact]
    public async Task AnUnknownRequestId_IsNotFound()
        => Assert.Equal(AmendmentReviewResult.NotFound,
            (await ReviewAsync(Guid.CreateVersion7(), approve: true)).Outcome.Result);

    /// <summary>
    /// The approver sees values from the moment of submission. If the record
    /// moved since, approving would record a previous value the register
    /// never held.
    /// </summary>
    [Fact]
    public async Task ApprovingAgainstAStaleRecord_IsAConflict()
    {
        var (first, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));

        // A second correction is approved first, moving the name underneath
        // the one still pending.
        var (second, _) = await AmendAsync(Amendment(childFullName: "Chipo M. Banda"));
        await ReviewAsync(second.Response!.AmendmentRequestId, approve: true);

        var (stale, _) = await ReviewAsync(first.Response!.AmendmentRequestId, approve: true);

        Assert.Equal(AmendmentReviewResult.Conflict, stale.Result);
        Assert.Contains("resubmitted", stale.Detail);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync(r => r.Brn == Brn);
        Assert.Equal("Chipo M. Banda", record.ChildPerson!.FullName);
    }

    /// <summary>
    /// Someone else making the same correction first is not a conflict -- the
    /// register already says what the reviewer is approving.
    /// </summary>
    [Fact]
    public async Task ApprovingAChangeSomeoneElseAlreadyMade_Succeeds()
    {
        var (first, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        var (second, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));

        await ReviewAsync(second.Response!.AmendmentRequestId, approve: true);
        var (duplicate, _) = await ReviewAsync(first.Response!.AmendmentRequestId, approve: true);

        Assert.True(duplicate.Succeeded);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync(r => r.Brn == Brn);
        Assert.Equal("Chipo Mwale-Banda", record.ChildPerson!.FullName);
    }

    // --- the split --------------------------------------------------------

    /// <summary>
    /// A birth-weight typo must not be held hostage by a name change that
    /// happened to share the same submission.
    /// </summary>
    [Fact]
    public async Task AMixedSubmission_SplitsAcrossBothTracks()
    {
        var (outcome, _) = await AmendAsync(
            Amendment(childFullName: "Chipo Mwale-Banda", birthWeightGrams: 3250));

        Assert.False(outcome.EverythingPending);
        Assert.Equal(nameof(AmendBirthRecordRequest.BirthWeightGrams),
            Assert.Single(outcome.Response!.Applied).Field);
        Assert.Equal(nameof(AmendBirthRecordRequest.ChildFullName),
            Assert.Single(outcome.Response.PendingApproval).Field);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync(r => r.Brn == Brn);

        Assert.Equal(3250, record.BirthWeightGrams);
        Assert.Equal("Chipo Mwale", record.ChildPerson!.FullName);
    }

    [Fact]
    public async Task BothHalvesOfAMixedSubmission_ShareOneRequestId()
    {
        var (outcome, _) = await AmendAsync(
            Amendment(childFullName: "Chipo Mwale-Banda", birthWeightGrams: 3250));

        await using var db = NewDb();
        var rows = await db.BirthRecordAmendments.ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(outcome.Response!.AmendmentRequestId, row.AmendmentRequestId));
    }

    // --- what carries over from before the two tracks ----------------------

    /// <summary>
    /// The point of the whole table: a register corrects by adding to the
    /// history, so what the record said before must still be provable.
    /// </summary>
    [Fact]
    public async Task ThePreviousValue_SurvivesTheCorrection()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        await using var db = NewDb();
        var amendment = await db.BirthRecordAmendments.SingleAsync();

        Assert.Equal(nameof(AmendBirthRecordRequest.ChildFullName), amendment.Field);
        Assert.Equal("Chipo Mwale", amendment.PreviousValue);
        Assert.Equal("Chipo Mwale-Banda", amendment.NewValue);
        Assert.Equal(Reason, amendment.Reason);
        Assert.Equal(RegistrarId, amendment.AmendedByRegistrarId);
        Assert.NotNull(amendment.AppliedAtUtc);
    }

    [Fact]
    public async Task AValueIdenticalToTheCurrentOne_RecordsNothing()
    {
        var (outcome, publisher) = await AmendAsync(Amendment(childFullName: "Chipo Mwale"));

        Assert.Equal(AmendmentResult.NothingToChange, outcome.Result);

        await using var db = NewDb();
        Assert.Empty(await db.BirthRecordAmendments.ToListAsync());
        Assert.Empty(publisher.Amendments);
    }

    [Fact]
    public async Task FieldsNotSupplied_AreLeftAlone()
    {
        await AmendAsync(Amendment(birthWeightGrams: 3250));

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync(r => r.Brn == Brn);

        Assert.Equal("Chipo Mwale", record.ChildPerson!.FullName);
        Assert.Equal(Sex.Female, record.Sex);
    }

    /// <summary>
    /// SQLite reads dates back as DateTimeKind.Unspecified. Treating that as
    /// local time would shift it and report an unchanged date of birth as
    /// corrected on every host not running in UTC.
    /// </summary>
    [Fact]
    public async Task TheSameDateOfBirth_IsNotReportedAsChanged()
        => Assert.Equal(AmendmentResult.NothingToChange,
            (await AmendAsync(Amendment(dateOfBirth: BornAt))).Outcome.Result);

    /// <summary>
    /// Date of birth is signed, so it queues -- and the round trip through
    /// its stored string form must reproduce the exact instant.
    /// </summary>
    [Fact]
    public async Task ACorrectedDateOfBirth_SurvivesTheApprovalRoundTrip()
    {
        var corrected = new DateTime(2026, 9, 11, 4, 30, 0, DateTimeKind.Utc);

        var (submitted, _) = await AmendAsync(Amendment(dateOfBirth: corrected));
        await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync(r => r.Brn == Brn);

        Assert.Equal(corrected, DateTime.SpecifyKind(record.DateOfBirth, DateTimeKind.Utc));
        Assert.Equal(DateOnly.FromDateTime(corrected), record.ChildPerson!.DateOfBirth);
    }

    [Fact]
    public async Task ACorrectedSex_SurvivesTheApprovalRoundTrip()
    {
        var (submitted, _) = await AmendAsync(Amendment(sex: Sex.Male));
        await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        await using var db = NewDb();
        Assert.Equal(Sex.Male, (await db.BirthRecords.SingleAsync(r => r.Brn == Brn)).Sex);
    }

    [Fact]
    public async Task AnImmediateAmendment_IsAudited()
    {
        await AmendAsync(Amendment(birthWeightGrams: 3250));

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(log => log.Action == "Amend");

        Assert.Equal(Brn, audit.EntityId);
        Assert.Equal(RegistrarId, audit.UserId);
        Assert.Equal("TABLET-07", audit.DeviceId);
    }

    [Fact]
    public async Task SubmissionAndApproval_AreAuditedSeparately()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        await using var db = NewDb();

        Assert.Equal(RegistrarId,
            (await db.AuditLogs.SingleAsync(log => log.Action == "AmendSubmitted")).UserId);
        Assert.Equal(ReviewerId,
            (await db.AuditLogs.SingleAsync(log => log.Action == "ApproveAmendment")).UserId);
    }

    // --- what is refused --------------------------------------------------

    [Fact]
    public async Task AnUnknownBrn_IsNotFound()
        => Assert.Equal(AmendmentResult.BirthRecordNotFound,
            (await AmendAsync(Amendment(birthWeightGrams: 3250), brn: "NO-SUCH-BRN")).Outcome.Result);

    [Fact]
    public async Task ARecordAtAnotherFacility_IsRefused()
        => Assert.Equal(AmendmentResult.NotPermitted,
            (await AmendAsync(Amendment(birthWeightGrams: 3250), brn: OtherFacilityBrn)).Outcome.Result);

    /// <summary>
    /// Correcting a record already ruled a duplicate would put the fix on the
    /// copy nobody uses, leaving the surviving record still wrong.
    /// </summary>
    [Fact]
    public async Task ASupersededRecord_IsNotTheOneCorrected()
    {
        var (outcome, _) = await AmendAsync(Amendment(birthWeightGrams: 3250), brn: SupersededBrn);

        Assert.Equal(AmendmentResult.RecordSuperseded, outcome.Result);

        await using var db = NewDb();
        Assert.Empty(await db.BirthRecordAmendments.ToListAsync());
    }

    [Fact]
    public async Task ADistrictOfficer_MayCorrectAnotherFacilitysRecord()
    {
        var (outcome, _) = await AmendAsync(
            Amendment(birthWeightGrams: 3250),
            brn: OtherFacilityBrn,
            roles: NcbrsRoles.DistrictOfficer);

        Assert.True(outcome.Succeeded);
    }

    // --- the certificate interaction --------------------------------------

    private async Task IssueCertificateAsync(string brn = Brn)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current, new DistrictLookup(db))
            .IssueAsync(brn, registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// The certificate stays good while a correction is merely proposed.
    /// Withdrawing on submission would let anyone invalidate a family's
    /// certificate by typing a name into a form.
    /// </summary>
    [Fact]
    public async Task APendingCorrection_DoesNotWithdrawTheCertificate()
    {
        await IssueCertificateAsync();

        var (outcome, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));

        Assert.False(outcome.Response!.CertificateInvalidated);

        await using var db = NewDb();
        Assert.True((await db.Certificates.SingleAsync()).IsValid);
        Assert.Empty(await db.CertificateRevocations.ToListAsync());
    }

    [Fact]
    public async Task ApprovingASignedFieldCorrection_WithdrawsTheCertificate()
    {
        await IssueCertificateAsync();

        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        var (review, _) = await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        Assert.True(review.Response!.CertificateInvalidated);

        await using var db = NewDb();
        var certificate = await db.Certificates.SingleAsync();

        Assert.False(certificate.IsValid);
        Assert.Contains(Reason, certificate.WithdrawnReason);
        Assert.Single(await db.CertificateRevocations.ToListAsync());
    }

    [Fact]
    public async Task RefusingACorrection_LeavesTheCertificateValid()
    {
        await IssueCertificateAsync();

        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        var (review, _) = await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: false);

        Assert.False(review.Response!.CertificateInvalidated);

        await using var db = NewDb();
        Assert.True((await db.Certificates.SingleAsync()).IsValid);
    }

    /// <summary>
    /// Birth weight is a statistical field the signature does not cover, so
    /// correcting it must not invalidate a document in a family's hands.
    /// </summary>
    [Fact]
    public async Task CorrectingAnUnsignedField_LeavesTheCertificateValid()
    {
        await IssueCertificateAsync();

        var (outcome, _) = await AmendAsync(Amendment(birthWeightGrams: 3250));

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.Response!.CertificateInvalidated);

        await using var db = NewDb();
        Assert.True((await db.Certificates.SingleAsync()).IsValid);
    }

    // --- the reviewer's queue ---------------------------------------------

    [Fact]
    public async Task PendingCorrections_AppearInTheQueueGroupedByRequest()
    {
        await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda", birthWeightGrams: 3250));

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);
        var queue = await Service(db, AuthTestContext.RegistrarService(db, http), new NoOpEventPublisher())
            .PendingAsync(null, new PageRequest());

        var item = Assert.Single(queue.Items);

        Assert.Equal(Brn, item.Brn);
        Assert.Equal("Nurse A. Banda", item.SubmittedByRegistrarName);
        Assert.Equal("Kabwe Village Health Post", item.FacilityName);

        // Only the half that actually needs a decision.
        Assert.Equal(nameof(AmendBirthRecordRequest.ChildFullName), Assert.Single(item.Changes).Field);
    }

    [Fact]
    public async Task AReviewedCorrection_LeavesTheQueue()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);
        var queue = await Service(db, AuthTestContext.RegistrarService(db, http), new NoOpEventPublisher())
            .PendingAsync(null, new PageRequest());

        Assert.Empty(queue.Items);
    }

    // --- the amended stream -----------------------------------------------

    /// <summary>
    /// Downstream systems care whose correction it was, not who signed it
    /// off, so the event is attributed to the author.
    /// </summary>
    [Fact]
    public async Task TheApprovedEvent_IsAttributedToTheAuthorNotTheReviewer()
    {
        var (submitted, _) = await AmendAsync(Amendment(childFullName: "Chipo Mwale-Banda"));
        var (_, publisher) = await ReviewAsync(submitted.Response!.AmendmentRequestId, approve: true);

        var published = Assert.Single(publisher.Amendments);

        Assert.Equal(RegistrarId, published.AmendedByRegistrarId);
        Assert.Equal(Reason, published.Reason);
        Assert.True(published.CertificateInvalidated == false);

        var change = Assert.Single(published.Changes);
        Assert.Equal("Chipo Mwale", change.PreviousValue);
        Assert.Equal("Chipo Mwale-Banda", change.NewValue);
    }

    [Fact]
    public async Task TheEvent_IsPartitionedByDistrict()
    {
        var (_, publisher) = await AmendAsync(Amendment(birthWeightGrams: 3250));

        Assert.Equal(("birth-record-amended", "D-CENTRAL-07"), Assert.Single(publisher.Enqueued));
    }

    /// <summary>
    /// The outbox row must commit with the correction it describes -- an
    /// amendment the registry applied but never published would leave every
    /// downstream copy silently stale.
    /// </summary>
    [Fact]
    public async Task TheOutboxRow_CommitsWithTheCorrection()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        await new AmendmentService(
                db,
                new NCBRS.Kafka.OutboxEventPublisher(
                    db, Options.Create(new NCBRS.Kafka.KafkaOptions { BootstrapServers = "unused" })),
                new CertificateRevocationRecorder(db),
                current,
                new DistrictLookup(db))
            .AmendAsync(Brn, Amendment(birthWeightGrams: 3250), registrar, Guid.CreateVersion7());

        await using var verify = NewDb();
        var message = await verify.OutboxMessages.SingleAsync();

        Assert.Equal("ncbrs.birth-records.amended", message.Topic);
        Assert.Equal("D-CENTRAL-07", message.PartitionKey);
        Assert.Contains("BirthWeightGrams", message.Payload);
    }
}

/// <summary>
/// The two rules with real weight: a correction must explain itself, and it
/// must actually correct something.
/// </summary>
public class AmendBirthRecordRequestValidatorTests
{
    private static readonly AmendBirthRecordRequestValidator Validator = new();

    private static AmendBirthRecordRequest Valid() => new()
    {
        ChildFullName = "Chipo Mwale-Banda",
        Reason = "Name misspelled on the original form.",
        DeviceId = "TABLET-07"
    };

    [Fact]
    public void AWellFormedAmendment_IsAccepted()
        => Assert.True(Validator.Validate(Valid()).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("typo")]
    public void AnAmendmentWithoutARealReason_IsRejected(string reason)
        => Assert.False(Validator.Validate(Valid() with { Reason = reason }).IsValid);

    [Fact]
    public void AnAmendmentChangingNothing_IsRejected()
    {
        var result = Validator.Validate(new AmendBirthRecordRequest
        {
            Reason = "Name misspelled on the original form.",
            DeviceId = "TABLET-07"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage.Contains("at least one field"));
    }

    [Fact]
    public void CorrectingANameToEmpty_IsRejected()
        => Assert.False(Validator.Validate(Valid() with { ChildFullName = "" }).IsValid);

    [Fact]
    public void AnAmendmentWithoutADevice_IsRejected()
        => Assert.False(Validator.Validate(Valid() with { DeviceId = "" }).IsValid);

    [Theory]
    [InlineData(100)]
    [InlineData(12000)]
    public void AnImpossibleBirthWeight_IsRejected(int grams)
        => Assert.False(Validator.Validate(Valid() with { BirthWeightGrams = grams }).IsValid);

    [Fact]
    public void AFutureDateOfBirth_IsRejected()
        => Assert.False(Validator.Validate(
            Valid() with { DateOfBirth = DateTime.UtcNow.AddDays(2) }).IsValid);

    [Fact]
    public void ABirthWeightNotSupplied_IsNotRangeChecked()
        => Assert.True(Validator.Validate(Valid() with { BirthWeightGrams = null }).IsValid);
}
