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
/// Covers voiding a registration that should never have existed.
///
/// The distinctions being defended: an amendment says the register described
/// a real birth wrongly; a duplicate supersession says two records describe
/// one child and one survives; this says there was no such birth, and
/// nothing survives. What that has to mean in practice is that every path
/// which acts on a record refuses, the certificate is revoked, downstream
/// systems are told to void rather than update -- and nothing is deleted.
/// </summary>
public class AnnulmentServiceTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid AdminId = Guid.Parse("0199a1b2-1003-7000-8000-000000000003");
    private const string AdminSubject = "33333333-3333-4333-8333-333333333333";

    private const string Brn = "100001";
    private const string Justification =
        "Filed against the wrong child during a training session; the infant named was never born at this facility.";

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly CertificateSigner _signer;

    public AnnulmentServiceTests()
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
            Name = "Kabwe Village Health Post",
            DistrictId = "D-CENTRAL-07",
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
                DisplayName = "Nurse A. Banda",
                CredentialHash = "test"
            },
            new Registrar
            {
                RegistrarId = AdminId,
                FacilityId = FacilityId,
                ExternalSubjectId = AdminSubject,
                DisplayName = "Ministry Admin P. Zulu",
                Role = RegistrarRole.MinistryAdmin
            });

        db.BirthRecords.Add(new BirthRecord
        {
            Brn = Brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Chipo Mwale" },
            FacilityId = FacilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = new DateTime(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc),
            Sex = Sex.Female,
            BirthWeightGrams = 3200,
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

    private static AnnulRecordRequest Request(
        AnnulmentReason reason = AnnulmentReason.RegisteredInError,
        string? authorityReference = null)
        => new()
        {
            Reason = reason,
            Justification = Justification,
            AuthorityReference = authorityReference
        };

    private async Task<(AnnulmentOutcome Outcome, NoOpEventPublisher Publisher)> AnnulAsync(
        AnnulRecordRequest? request = null, string brn = Brn)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(AdminSubject, NcbrsRoles.MinistryAdmin);
        var current = AuthTestContext.RegistrarService(db, http);
        var publisher = new NoOpEventPublisher();
        var admin = db.Registrars.Single(r => r.RegistrarId == AdminId);

        var outcome = await new AnnulmentService(
                db, publisher, new CertificateRevocationRecorder(db), current)
            .AnnulAsync(brn, request ?? Request(), admin, Guid.CreateVersion7());

        return (outcome, publisher);
    }

    private async Task IssueCertificateAsync()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current)
            .IssueAsync(Brn, registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.True(result.Succeeded);
    }

    // --- the act ----------------------------------------------------------

    [Fact]
    public async Task AnnullingARecord_VoidsIt()
    {
        var (outcome, _) = await AnnulAsync();

        Assert.True(outcome.Succeeded);
        Assert.Equal(RecordStatus.Annulled, outcome.Response!.Status);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal(RecordStatus.Annulled, record.Status);
        Assert.NotNull(record.AnnulledAtUtc);
    }

    /// <summary>
    /// Nothing is deleted, and the number is never returned to the pool. A
    /// BRN that has circulated must keep resolving to an explanation.
    /// </summary>
    [Fact]
    public async Task TheRecordAndItsBrn_SurviveTheAnnulment()
    {
        await AnnulAsync();

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync();

        Assert.Equal(Brn, record.Brn);
        Assert.Equal("Chipo Mwale", record.ChildPerson!.FullName);
        Assert.Single(await db.RecordAnnulments.ToListAsync());
    }

    [Fact]
    public async Task TheAnnulment_RecordsItsReasonAndAuthor()
    {
        await AnnulAsync(Request(AnnulmentReason.FraudulentRegistration));

        await using var db = NewDb();
        var annulment = await db.RecordAnnulments.SingleAsync();

        Assert.Equal(AnnulmentReason.FraudulentRegistration, annulment.Reason);
        Assert.Equal(Justification, annulment.Justification);
        Assert.Equal(AdminId, annulment.AnnulledByRegistrarId);
    }

    [Fact]
    public async Task AnnullingTwice_IsRefused()
    {
        await AnnulAsync();

        var (second, publisher) = await AnnulAsync();

        Assert.Equal(AnnulmentResult.AlreadyAnnulled, second.Result);
        Assert.Empty(publisher.Annulments);
    }

    [Fact]
    public async Task AnUnknownBrn_IsNotFound()
        => Assert.Equal(AnnulmentResult.BirthRecordNotFound,
            (await AnnulAsync(brn: "NO-SUCH-BRN")).Outcome.Result);

    /// <summary>
    /// A court-ordered annulment that cannot name its order is not a
    /// court-ordered annulment.
    /// </summary>
    [Fact]
    public async Task ACourtOrderedAnnulmentWithoutTheOrder_IsRefused()
    {
        var (outcome, _) = await AnnulAsync(Request(AnnulmentReason.CourtOrdered));

        Assert.Equal(AnnulmentResult.AuthorityReferenceRequired, outcome.Result);

        await using var db = NewDb();
        Assert.Equal(RecordStatus.Confirmed, (await db.BirthRecords.SingleAsync()).Status);
    }

    [Fact]
    public async Task ACourtOrderedAnnulmentCitingTheOrder_Succeeds()
    {
        var (outcome, _) = await AnnulAsync(
            Request(AnnulmentReason.CourtOrdered, "High Court order HC/2026/441"));

        Assert.True(outcome.Succeeded);

        await using var db = NewDb();
        Assert.Equal("High Court order HC/2026/441",
            (await db.RecordAnnulments.SingleAsync()).AuthorityReference);
    }

    [Fact]
    public async Task AnAnnulment_IsAudited()
    {
        await AnnulAsync();

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(log => log.Action == "Annul");

        Assert.Equal(Brn, audit.EntityId);
        Assert.Equal(AdminId, audit.UserId);
    }

    // --- the certificate ---------------------------------------------------

    [Fact]
    public async Task AnnullingARecordWithACertificate_RevokesIt()
    {
        await IssueCertificateAsync();

        var (outcome, _) = await AnnulAsync();

        Assert.True(outcome.Response!.CertificateRevoked);

        await using var db = NewDb();
        var certificate = await db.Certificates.SingleAsync();

        Assert.False(certificate.IsValid);
        Assert.Equal(RevocationReason.RegistrationAnnulled,
            (await db.CertificateRevocations.SingleAsync()).Reason);
    }

    /// <summary>
    /// Unlike an amendment, this revokes whatever the signature covers: the
    /// document certifies a birth the register no longer holds at all.
    /// </summary>
    [Fact]
    public async Task ARevokedCertificate_StopsVerifying()
    {
        await IssueCertificateAsync();

        string qr;
        await using (var db = NewDb())
        {
            qr = (await db.Certificates.SingleAsync()).QrPayload;
        }

        await AnnulAsync();

        await using var verify = NewDb();
        var result = await new CertificateRevocationService(
                verify, _signer, Options.Create(new CertificateRevocationOptions()))
            .VerifyAsync(qr);

        Assert.False(result.Valid);
        Assert.True(result.Revoked);
        Assert.Equal(RevocationReason.RegistrationAnnulled, result.RevocationReason);
    }

    [Fact]
    public async Task AnnullingARecordWithNoCertificate_RevokesNothing()
    {
        var (outcome, _) = await AnnulAsync();

        Assert.False(outcome.Response!.CertificateRevoked);

        await using var db = NewDb();
        Assert.Empty(await db.CertificateRevocations.ToListAsync());
    }

    // --- what an annulled record refuses -----------------------------------

    [Fact]
    public async Task AnAnnulledRecord_CannotBeCertified()
    {
        await AnnulAsync();

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current)
            .IssueAsync(Brn, registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.Equal(CertificateResult.RecordAnnulled, result.Result);
    }

    /// <summary>
    /// Otherwise the withdrawn-certificate branch would tell a clerk to issue
    /// a replacement, which annulment makes impossible.
    /// </summary>
    [Fact]
    public async Task AnAnnulledRecordsCertificate_CannotBeReprinted()
    {
        await IssueCertificateAsync();
        await AnnulAsync();

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current)
            .ReprintAsync(Brn, registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.Equal(CertificateResult.RecordAnnulled, result.Result);
        Assert.Contains("cannot be reprinted", result.Detail);
    }

    /// <summary>
    /// A record that should never have existed cannot be corrected into one
    /// that should.
    /// </summary>
    [Fact]
    public async Task AnAnnulledRecord_CannotBeAmended()
    {
        await AnnulAsync();

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new AmendmentService(
                db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current)
            .AmendAsync(Brn, new AmendBirthRecordRequest
            {
                BirthWeightGrams = 3250,
                Reason = "Scale re-read at the bedside.",
                DeviceId = "TABLET-07"
            }, registrar, Guid.CreateVersion7());

        Assert.Equal(AmendmentResult.RecordAnnulled, result.Result);
    }

    /// <summary>
    /// An outcome attaches a death to a birth event. A voided registration
    /// describes no birth event, so the attachment would assert something the
    /// register has withdrawn.
    /// </summary>
    [Fact]
    public async Task AnAnnulledRecord_CannotTakeAnOutcome()
    {
        await AnnulAsync();

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new OutcomeService(db, new NoOpEventPublisher(), current)
            .RecordNeonatalAsync(Brn, new RecordNeonatalOutcomeRequest
            {
                DeathDateUtc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
                IcdPmTiming = IcdPmTiming.Neonatal,
                IcdPmCauseCode = "N1",
                DeviceId = "TABLET-07"
            }, registrar, Guid.CreateVersion7());

        Assert.Equal(OutcomeResult.RecordAnnulled, result.Result);
        Assert.Contains("annulled", result.Detail);
    }

    /// <summary>
    /// A voided record describes nobody, so matching a new registration
    /// against it would flag a duplicate of a child who does not exist.
    /// </summary>
    [Fact]
    public async Task AnAnnulledRecord_IsNotMatchedForDuplicates()
    {
        await AnnulAsync();

        await using var db = NewDb();
        var annulled = await db.BirthRecords.SingleAsync();

        var twin = new BirthRecord
        {
            Brn = "100002",
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Chipo Mwale" },
            FacilityId = FacilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = annulled.DateOfBirth,
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton
        };

        db.BirthRecords.Add(twin);
        await db.SaveChangesAsync();

        var flagged = await new DuplicateDetectionService(
                db, new DuplicateMatcher(), new CertificateRevocationRecorder(db),
                NullLogger<DuplicateDetectionService>.Instance)
            .ScanAsync(twin.BirthRecordId);

        Assert.Equal(0, flagged);
    }

    // --- the annulled stream -----------------------------------------------

    /// <summary>
    /// The instruction downstream is "void", not "update". Publishing this on
    /// the amendment topic would leave a National ID record standing for an
    /// identity the register has withdrawn.
    /// </summary>
    [Fact]
    public async Task AnAnnulment_IsPublishedOnItsOwnTopic()
    {
        var (_, publisher) = await AnnulAsync();

        Assert.Equal(("birth-record-annulled", "D-CENTRAL-07"), Assert.Single(publisher.Enqueued));
        Assert.Empty(publisher.Amendments);

        var published = Assert.Single(publisher.Annulments);

        Assert.Equal(Brn, published.Brn);
        Assert.Equal(nameof(AnnulmentReason.RegisteredInError), published.Reason);
        Assert.Equal(Justification, published.Justification);
        Assert.False(published.CertificateRevoked);
    }

    [Fact]
    public async Task TheEvent_SaysWhetherACertificateWasRevoked()
    {
        await IssueCertificateAsync();

        var (_, publisher) = await AnnulAsync();

        Assert.True(Assert.Single(publisher.Annulments).CertificateRevoked);
    }

    [Fact]
    public async Task TheOutboxRow_CommitsWithTheAnnulment()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(AdminSubject, NcbrsRoles.MinistryAdmin);
        var current = AuthTestContext.RegistrarService(db, http);
        var admin = db.Registrars.Single(r => r.RegistrarId == AdminId);

        await new AnnulmentService(
                db,
                new NCBRS.Kafka.OutboxEventPublisher(
                    db, Options.Create(new NCBRS.Kafka.KafkaOptions { BootstrapServers = "unused" })),
                new CertificateRevocationRecorder(db),
                current)
            .AnnulAsync(Brn, Request(), admin, Guid.CreateVersion7());

        await using var verify = NewDb();
        var message = await verify.OutboxMessages.SingleAsync();

        Assert.Equal("ncbrs.birth-records.annulled", message.Topic);
        Assert.Equal("D-CENTRAL-07", message.PartitionKey);
    }
}

/// <summary>
/// The justification is the document an appeal or an investigation reads
/// years later, so it is held to a higher bar than an amendment's reason.
/// </summary>
public class AnnulRecordRequestValidatorTests
{
    private static readonly AnnulRecordRequestValidator Validator = new();

    private static AnnulRecordRequest Valid() => new()
    {
        Reason = AnnulmentReason.RegisteredInError,
        Justification = "Filed against the wrong child during a training session; no such birth occurred."
    };

    [Fact]
    public void AWellFormedAnnulment_IsAccepted()
        => Assert.True(Validator.Validate(Valid()).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("wrong child")]
    [InlineData("entered in error")]
    public void AThinJustification_IsRejected(string justification)
        => Assert.False(Validator.Validate(Valid() with { Justification = justification }).IsValid);

    [Fact]
    public void ACourtOrderWithoutAReference_IsRejected()
    {
        var result = Validator.Validate(Valid() with { Reason = AnnulmentReason.CourtOrdered });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage.Contains("cite the order"));
    }

    [Fact]
    public void ACourtOrderWithAReference_IsAccepted()
        => Assert.True(Validator.Validate(Valid() with
        {
            Reason = AnnulmentReason.CourtOrdered,
            AuthorityReference = "High Court order HC/2026/441"
        }).IsValid);
}
