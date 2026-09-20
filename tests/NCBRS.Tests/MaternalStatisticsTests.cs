using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the statistical questionnaire (draft 6.5.1; UN P&amp;R Rev. 3).
///
/// The separation from the legal record is what most of these defend. It is
/// not just a table boundary: the questionnaire must never gate a
/// certificate, never block a registration, and never reach the printed
/// document. A birth whose questionnaire is blank is a fully registered
/// birth.
///
/// What it does have to do is refuse answers that contradict each other,
/// because a self-contradictory questionnaire aggregates silently into a
/// national figure nobody can tell is wrong.
/// </summary>
public class MaternalStatisticsServiceTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid AdminId = Guid.Parse("0199a1b2-1003-7000-8000-000000000003");
    private const string AdminSubject = "33333333-3333-4333-8333-333333333333";

    private const string Brn = "100001";
    private static readonly DateTime BornAt = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly CertificateSigner _signer;

    public MaternalStatisticsServiceTests()
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
            BrnBlockNextAvailable = 100_200,
            BrnBlockEnd = 199_999
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
                RegistrarId = AdminId,
                FacilityId = FacilityId,
                ExternalSubjectId = AdminSubject,
                DisplayName = "Ministry Admin P. Lako",
                Role = RegistrarRole.MinistryAdmin
            });

        db.BirthRecords.Add(new BirthRecord
        {
            Brn = Brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Ayen Deng" },
            FacilityId = FacilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = BornAt,
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            Status = RecordStatus.Confirmed,
            ConfirmedAtUtc = DateTime.UtcNow
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

    private static MaternalStatisticsRequest Questionnaire(
        int priorLiveBirths = 2,
        int priorFetalDeaths = 0,
        int? prenatalVisits = 8,
        DateOnly? lastLiveBirth = null,
        DateOnly? careBegan = null)
        => new()
        {
            MotherEducationLevel = EducationLevel.LowerSecondary,
            MotherOccupation = OccupationGroup.SkilledAgriculturalForestryAndFishery,
            FatherEducationLevel = EducationLevel.Primary,
            FatherOccupation = OccupationGroup.ElementaryOccupations,
            PriorLiveBirths = priorLiveBirths,
            PriorFetalDeaths = priorFetalDeaths,
            PrenatalVisitCount = prenatalVisits,
            MedicalCareBeganDate = careBegan,
            DateOfLastLiveBirth = lastLiveBirth
        };

    private async Task<MaternalStatisticsOutcome> CaptureAsync(
        MaternalStatisticsRequest? request = null, string brn = Brn)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        return await new MaternalStatisticsService(db, current, new CountyLookup(db))
            .CaptureAsync(brn, request ?? Questionnaire(), registrar, "TABLET-07", Guid.CreateVersion7());
    }

    // --- capture ----------------------------------------------------------

    [Fact]
    public async Task AQuestionnaire_IsRecorded()
    {
        var outcome = await CaptureAsync();

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.Revised);

        await using var db = NewDb();
        var statistics = await db.MaternalStatistics.SingleAsync();

        Assert.Equal(EducationLevel.LowerSecondary, statistics.MotherEducationLevel);
        Assert.Equal(OccupationGroup.SkilledAgriculturalForestryAndFishery, statistics.MotherOccupation);
        Assert.Equal(2, statistics.PriorLiveBirths);
        Assert.Equal(RegistrarId, statistics.RecordedByRegistrarId);
    }

    /// <summary>
    /// A statistics clerk completing what a midwife left blank is doing the
    /// same act, not a second one -- and needs no approval, because this is
    /// not the legal record.
    /// </summary>
    [Fact]
    public async Task ResubmittingTheQuestionnaire_RevisesItInPlace()
    {
        await CaptureAsync();

        var revised = await CaptureAsync(Questionnaire(priorLiveBirths: 3, prenatalVisits: 10));

        Assert.True(revised.Succeeded);
        Assert.True(revised.Revised);

        await using var db = NewDb();
        var statistics = await db.MaternalStatistics.SingleAsync();

        Assert.Equal(3, statistics.PriorLiveBirths);
        Assert.Equal(10, statistics.PrenatalVisitCount);
        Assert.NotNull(statistics.UpdatedAtUtc);
    }

    [Fact]
    public async Task CaptureAndRevision_AreAuditedDistinctly()
    {
        await CaptureAsync();
        await CaptureAsync();

        await using var db = NewDb();

        Assert.Equal(1, await db.AuditLogs.CountAsync(log => log.Action == "RecordMaternalStatistics"));
        Assert.Equal(1, await db.AuditLogs.CountAsync(log => log.Action == "ReviseMaternalStatistics"));
    }

    /// <summary>
    /// A registrar who cannot get an answer must be able to move on rather
    /// than invent one, so a half-answered questionnaire is accepted.
    /// </summary>
    [Fact]
    public async Task APartlyAnsweredQuestionnaire_IsAccepted()
    {
        var outcome = await CaptureAsync(new MaternalStatisticsRequest
        {
            MotherEducationLevel = EducationLevel.NotStated,
            PriorLiveBirths = 0,
            PriorFetalDeaths = 0
        });

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Response!.PrenatalVisitCount);
        Assert.Null(outcome.Response.MeetsWhoAntenatalMinimum);
    }

    [Fact]
    public async Task AnUnknownBrn_IsNotFound()
        => Assert.Equal(MaternalStatisticsResult.BirthRecordNotFound,
            (await CaptureAsync(brn: "NO-SUCH-BRN")).Result);

    // --- consistency ------------------------------------------------------

    /// <summary>
    /// The clearest self-contradiction: a previous live birth reported
    /// alongside a count of zero previous live births.
    /// </summary>
    [Fact]
    public async Task ALastLiveBirthWithNoPriorLiveBirths_IsRefused()
    {
        var outcome = await CaptureAsync(Questionnaire(
            priorLiveBirths: 0, lastLiveBirth: new DateOnly(2024, 3, 1)));

        Assert.Equal(MaternalStatisticsResult.Inconsistent, outcome.Result);
        Assert.Contains("priorLiveBirths is 0", outcome.Detail);
    }

    [Fact]
    public async Task ALastLiveBirthAfterThisOne_IsRefused()
    {
        var outcome = await CaptureAsync(Questionnaire(
            lastLiveBirth: DateOnly.FromDateTime(BornAt).AddDays(1)));

        Assert.Equal(MaternalStatisticsResult.Inconsistent, outcome.Result);
        Assert.Contains("must fall before this birth", outcome.Detail);
    }

    [Fact]
    public async Task AntenatalCareBeginningAfterTheBirth_IsRefused()
    {
        var outcome = await CaptureAsync(Questionnaire(
            careBegan: DateOnly.FromDateTime(BornAt).AddDays(3)));

        Assert.Equal(MaternalStatisticsResult.Inconsistent, outcome.Result);
        Assert.Contains("cannot fall after the birth", outcome.Detail);
    }

    /// <summary>
    /// Care beginning before the pregnancy could have started is a
    /// data-entry error, not an unusually early booking.
    /// </summary>
    [Fact]
    public async Task AntenatalCareBeginningBeforeAnyPlausiblePregnancy_IsRefused()
    {
        var outcome = await CaptureAsync(Questionnaire(
            careBegan: DateOnly.FromDateTime(BornAt).AddYears(-2)));

        Assert.Equal(MaternalStatisticsResult.Inconsistent, outcome.Result);
        Assert.Contains("outside any plausible pregnancy", outcome.Detail);
    }

    /// <summary>
    /// The booking visit is itself an antenatal contact, so care that began
    /// cannot have produced zero of them.
    /// </summary>
    [Fact]
    public async Task CareThatBeganWithZeroContacts_IsRefused()
    {
        var outcome = await CaptureAsync(Questionnaire(
            prenatalVisits: 0, careBegan: DateOnly.FromDateTime(BornAt).AddMonths(-6)));

        Assert.Equal(MaternalStatisticsResult.Inconsistent, outcome.Result);
        Assert.Contains("booking visit", outcome.Detail);
    }

    [Fact]
    public async Task AConsistentQuestionnaireWithDates_IsAccepted()
    {
        var outcome = await CaptureAsync(Questionnaire(
            lastLiveBirth: new DateOnly(2024, 3, 10),
            careBegan: DateOnly.FromDateTime(BornAt).AddMonths(-7)));

        Assert.True(outcome.Succeeded);
    }

    // --- derived indicators ------------------------------------------------

    /// <summary>
    /// Birth spacing is a WHO indicator in its own right, so it is returned
    /// rather than left for every consumer to recompute from two dates.
    /// </summary>
    [Fact]
    public async Task TheBirthInterval_IsDerivedFromTheDates()
    {
        // 2024-03-10 to 2026-09-10 is thirty whole months.
        var outcome = await CaptureAsync(Questionnaire(lastLiveBirth: new DateOnly(2024, 3, 10)));

        Assert.Equal(30, outcome.Response!.BirthIntervalMonths);
    }

    /// <summary>
    /// Counted on calendar months rather than by dividing days, so the
    /// 24-month WHO threshold lands exactly where it should.
    /// </summary>
    [Fact]
    public async Task TheBirthInterval_CountsWholeCalendarMonths()
    {
        var outcome = await CaptureAsync(Questionnaire(lastLiveBirth: new DateOnly(2024, 9, 11)));

        // One day short of 24 months is 23, not 24.
        Assert.Equal(23, outcome.Response!.BirthIntervalMonths);
    }

    [Fact]
    public async Task WithNoPreviousBirth_ThereIsNoInterval()
        => Assert.Null((await CaptureAsync(Questionnaire(priorLiveBirths: 0))).Response!.BirthIntervalMonths);

    [Theory]
    [InlineData(8, true)]
    [InlineData(12, true)]
    [InlineData(7, false)]
    [InlineData(0, false)]
    public async Task WhoAntenatalCoverage_IsReportedAgainstTheEightContactMinimum(int visits, bool expected)
    {
        var outcome = await CaptureAsync(Questionnaire(prenatalVisits: visits));

        Assert.Equal(expected, outcome.Response!.MeetsWhoAntenatalMinimum);
    }

    // --- the separation from the legal record ------------------------------

    /// <summary>
    /// The heart of draft 6.5.1. A birth whose questionnaire is blank is a
    /// fully registered birth, and its certificate must issue normally.
    /// </summary>
    [Fact]
    public async Task ARecordWithNoQuestionnaire_IsStillCertifiable()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current, new CountyLookup(db))
            .IssueAsync(Brn, registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// Nothing from the questionnaire may reach the printed document: the
    /// certificate attests a birth, not a household survey.
    /// </summary>
    [Fact]
    public async Task NothingFromTheQuestionnaire_ReachesTheCertificate()
    {
        await CaptureAsync();

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current, new CountyLookup(db))
            .IssueAsync(Brn, registrar, "TABLET-07", Guid.CreateVersion7());

        var payload = _signer.Verify(result.Response!.QrPayload);

        Assert.NotNull(payload);
        Assert.DoesNotContain("LowerSecondary", payload);
        Assert.DoesNotContain("Agricultural", payload);
    }

    /// <summary>
    /// Captured in one workflow (draft 6.5.1) but never able to fail the
    /// registration: a birth must not go unregistered over an answer about
    /// the mother's schooling.
    /// </summary>
    [Fact]
    public async Task AQuestionnaireSuppliedWithTheRegistration_IsCaptured()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new BirthRegistrationService(
                db, new NoOpEventPublisher(), current,
                new DuplicateDetectionService(db, new DuplicateMatcher(),
                    new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance,
                    new CountyLookup(db)),
                new CountyLookup(db),
                Options.Create(new StatutoryRegistrationOptions()))
            .RegisterAsync(new RegisterBirthRequest
            {
                Brn = "100002",
                FacilityId = FacilityId,
                ChildFullName = "Nyasha Lado",
                DateOfBirth = DateTime.UtcNow.Date.AddDays(-3),
                Sex = Sex.Male,
                Plurality = BirthPlurality.Singleton,
                DeviceId = "TABLET-07",
                MaternalStatistics = Questionnaire(prenatalVisits: 9)
            }, registrar, Guid.CreateVersion7());

        Assert.True(result.Succeeded);

        await using var verify = NewDb();
        var statistics = await verify.MaternalStatistics
            .SingleAsync(s => s.BirthRecordId == result.Record!.BirthRecordId);

        Assert.Equal(9, statistics.PrenatalVisitCount);
    }

    [Fact]
    public async Task AnAnnulledRecord_TakesNoQuestionnaire()
    {
        await using (var annul = NewDb())
        {
            var http = AuthTestContext.HttpContextFor(AdminSubject, NcbrsRoles.MinistryAdmin);
            var current = AuthTestContext.RegistrarService(annul, http);
            var admin = annul.Registrars.Single(r => r.RegistrarId == AdminId);

            var outcome = await new AnnulmentService(
                    annul, new NoOpEventPublisher(), new CertificateRevocationRecorder(annul), current,
                    new CountyLookup(annul))
                .AnnulAsync(Brn, new AnnulRecordRequest
                {
                    Reason = AnnulmentReason.RegisteredInError,
                    Justification = "Filed against the wrong child during a training session; no such birth occurred."
                }, admin, Guid.CreateVersion7());

            Assert.True(outcome.Succeeded);
        }

        Assert.Equal(MaternalStatisticsResult.RecordAnnulled, (await CaptureAsync()).Result);
    }
}

/// <summary>
/// Ranges reject the physically impossible, not the merely unusual: a
/// questionnaire that refuses a real answer produces a gap in the national
/// figures, which is worse than an outlier in them.
/// </summary>
public class MaternalStatisticsRequestValidatorTests
{
    private static readonly MaternalStatisticsRequestValidator Validator = new();

    private static MaternalStatisticsRequest Valid() => new()
    {
        MotherEducationLevel = EducationLevel.Primary,
        PriorLiveBirths = 1,
        PriorFetalDeaths = 0,
        PrenatalVisitCount = 8
    };

    [Fact]
    public void AWellFormedQuestionnaire_IsAccepted()
        => Assert.True(Validator.Validate(Valid()).IsValid);

    [Fact]
    public void AnEmptyQuestionnaire_IsAccepted()
        => Assert.True(Validator.Validate(new MaternalStatisticsRequest()).IsValid);

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void AnImpossiblePriorLiveBirthCount_IsRejected(int count)
        => Assert.False(Validator.Validate(Valid() with { PriorLiveBirths = count }).IsValid);

    /// <summary>
    /// A high-risk pregnancy under close follow-up can far exceed WHO's
    /// minimum of eight, so a large count must not be refused.
    /// </summary>
    [Fact]
    public void AnUnusuallyHighButPossibleVisitCount_IsAccepted()
        => Assert.True(Validator.Validate(Valid() with { PrenatalVisitCount = 40 }).IsValid);

    [Fact]
    public void AnImpossibleVisitCount_IsRejected()
        => Assert.False(Validator.Validate(Valid() with { PrenatalVisitCount = 61 }).IsValid);

    [Fact]
    public void AFutureLastLiveBirth_IsRejected()
        => Assert.False(Validator.Validate(Valid() with
        {
            DateOfLastLiveBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1))
        }).IsValid);

    [Fact]
    public void ADeviceIsRequired_OnTheStandaloneEndpoint()
        => Assert.False(new CaptureMaternalStatisticsRequestValidator()
            .Validate(new CaptureMaternalStatisticsRequest { PriorLiveBirths = 1 }).IsValid);
}
