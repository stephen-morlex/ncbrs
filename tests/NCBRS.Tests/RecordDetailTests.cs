using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// What looking a record up actually returns.
///
/// Three things the record held and the response did not say, each of which a
/// registrar is asked about with a family in front of them: that the
/// registration was late and its certificate is being withheld, that the
/// number on the slip in their hand is this record's, and whether a
/// certificate exists and still stands.
/// </summary>
public class RecordDetailTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private static readonly DateTime Born = new(2026, 1, 5, 4, 30, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public RecordDetailTests()
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
        });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Lado",
        });

        db.SaveChanges();
    }

    [Fact]
    public async Task ALateRegistration_SaysSoWhenTheRecordIsLookedUp()
    {
        // The regression this file exists for. The field was on the response
        // and populated only when registering -- so the screen's "late
        // registration" badge could never appear on a lookup, and a family
        // whose certificate is being withheld pending verification would have
        // been sent away with no idea why.
        GivenARecord();

        await using (var seed = new NcbrsDbContext(_options))
        {
            seed.LateRegistrations.Add(new LateRegistration
            {
                BirthRecordId = seed.BirthRecords.Single().BirthRecordId,
                DaysLate = 400,
                WindowDaysAtFiling = 90,
                EvidenceType = LateRegistrationEvidenceType.BirthAttendantAttestation,
                DeclarantName = "Nyandeng Deng",
                DeclarantRelationship = "mother",
                SubmittedByRegistrarId = RegistrarId,
            });

            await seed.SaveChangesAsync();
        }

        var record = await LookUpAsync("100001");

        Assert.NotNull(record.LateRegistration);
        Assert.Equal(400, record.LateRegistration!.DaysLate);
        Assert.Equal(90, record.LateRegistration.WindowDaysAtFiling);

        // Pending, so no certificate. The pair is the point: late *and* not
        // yet verified is what withholds the document.
        Assert.Equal(LateRegistrationStatus.PendingApproval, record.LateRegistration.Status);
        Assert.Null(record.Certificate);
    }

    [Fact]
    public async Task AProvisionalIdentifier_IsReturnedSoTheSlipInAFamilysHandMatches()
    {
        // Lookup already resolved on this value; the response never said it
        // existed. A clerk handed a PROV- slip found the record and saw only
        // a BRN, with nothing confirming the two were the same registration.
        GivenARecord(provisionalIdentifier: "PROV-TABLET07-42");

        var byBrn = await LookUpAsync("100001");
        var bySlip = await LookUpAsync("PROV-TABLET07-42");

        Assert.Equal("PROV-TABLET07-42", byBrn.ProvisionalIdentifier);

        // And the same record answers to both, which is the whole reason the
        // identifier is retained after a real BRN is assigned.
        Assert.Equal(byBrn.BirthRecordId, bySlip.BirthRecordId);
        Assert.Equal("100001", bySlip.Brn);
    }

    [Fact]
    public async Task AWithdrawnCertificate_IsNeverShownAsSimplyIssued()
    {
        // A certificate is valid only if signed AND not revoked. Issuance and
        // withdrawal travel together so no screen can render the first
        // without the second and state something false about a legal
        // document.
        GivenARecord();
        GivenACertificate(withdrawn: true);

        var record = await LookUpAsync("100001");

        Assert.NotNull(record.Certificate);
        Assert.False(record.Certificate!.IsValid);
        Assert.NotNull(record.Certificate.WithdrawnAtUtc);
        Assert.Equal("Superseded by an amendment.", record.Certificate.WithdrawnReason);
    }

    [Fact]
    public async Task AValidCertificate_ReportsItselfValid()
    {
        GivenARecord();
        GivenACertificate(withdrawn: false);

        var record = await LookUpAsync("100001");

        Assert.NotNull(record.Certificate);
        Assert.True(record.Certificate!.IsValid);
        Assert.Null(record.Certificate.WithdrawnAtUtc);
    }

    [Fact]
    public async Task ARecordWithNoCertificate_ReportsNoneRatherThanFailing()
    {
        // The common case, and not a fault: a record whose BRN is still
        // provisional, or whose late registration is unverified, has no
        // certificate yet and is supposed not to.
        GivenARecord();

        var record = await LookUpAsync("100001");

        Assert.Null(record.Certificate);
    }

    [Fact]
    public async Task WhenACertificateWasReplaced_TheCurrentOneIsReported()
    {
        // A record accumulates certificates: an amendment withdraws the one it
        // contradicts and a replacement is issued. The latest is the one the
        // family is holding and the one they are asking about.
        GivenARecord();
        GivenACertificate(withdrawn: true, issuedAt: Born.AddDays(10));
        GivenACertificate(withdrawn: false, issuedAt: Born.AddDays(60));

        var record = await LookUpAsync("100001");

        Assert.NotNull(record.Certificate);
        Assert.True(record.Certificate!.IsValid);
        Assert.Equal(Born.AddDays(60), record.Certificate.IssuedAtUtc);
    }

    [Fact]
    public async Task EverythingACorrectionCanChange_IsReturned()
    {
        // A registrar cannot correct a value they cannot see. Five of the eight
        // correctable fields were stored and published nowhere, so a correction
        // form could offer only blank boxes -- and a birth weight retyped from
        // memory is a second guess, not a correction.
        GivenARecord(withParentsAndMeasurements: true);

        var record = await LookUpAsync("100001");

        Assert.Equal("Nyandeng Deng", record.MotherFullName);
        Assert.Equal("John Deng", record.FatherFullName);
        Assert.Equal(3200, record.BirthWeightGrams);
        Assert.Equal(39.5m, record.GestationalAgeWeeks);
        Assert.Equal(1, record.BirthOrder);
    }

    [Fact]
    public async Task AnUnmeasuredValue_ComesBackNullRatherThanZero()
    {
        // A village post with no scale records no weight. Zero would render as
        // a number a registrar might leave in place, thereby asserting it.
        GivenARecord();

        var record = await LookUpAsync("100001");

        Assert.Null(record.BirthWeightGrams);
        Assert.Null(record.GestationalAgeWeeks);
        Assert.Null(record.MotherFullName);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task TheStatutoryWindowIsPublished_FromConfigurationAndNotAConstant(int windowDays)
    {
        // A client that hardcodes this does not fail when the Act is amended —
        // it quietly stops asking for evidence on births that now need it, and
        // nothing says so. Published so the form can ask instead of assuming,
        // and parameterised so a constant slipped in here would fail.
        await using var db = new NcbrsDbContext(_options);
        var http = AuthTestContext.HttpContextFor();

        var rules = Controller(db, http, windowDays).GetRegistrationRules();

        Assert.Equal(windowDays, rules.Value!.StatutoryWindowDays);
    }

    private void GivenARecord(
        string? provisionalIdentifier = null,
        bool withParentsAndMeasurements = false)
    {
        using var db = new NcbrsDbContext(_options);

        db.BirthRecords.Add(new BirthRecord
        {
            MotherPerson = withParentsAndMeasurements
                ? new Person { FullName = "Nyandeng Deng" }
                : null,
            FatherPerson = withParentsAndMeasurements
                ? new Person { FullName = "John Deng" }
                : null,
            BirthWeightGrams = withParentsAndMeasurements ? 3200 : null,
            GestationalAgeWeeks = withParentsAndMeasurements ? 39.5m : null,
            BirthOrder = withParentsAndMeasurements ? 1 : null,
            Brn = "100001",
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = "Ayen Deng" },
            FacilityId = FacilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = Born,
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            Status = RecordStatus.Confirmed,
            ProvisionalIdentifier = provisionalIdentifier,
        });

        db.SaveChanges();
    }

    private void GivenACertificate(bool withdrawn, DateTime? issuedAt = null)
    {
        using var db = new NcbrsDbContext(_options);

        db.Certificates.Add(new Certificate
        {
            BirthRecordId = db.BirthRecords.Single().BirthRecordId,
            IssueDateUtc = issuedAt ?? Born.AddDays(7),
            QrPayload = "payload",
            SignatureHash = "hash",
            WithdrawnAtUtc = withdrawn ? Born.AddDays(90) : null,
            WithdrawnReason = withdrawn ? "Superseded by an amendment." : null,
        });

        db.SaveChanges();
    }

    private async Task<BirthRecordResponse> LookUpAsync(string brn)
    {
        await using var db = new NcbrsDbContext(_options);
        var http = AuthTestContext.HttpContextFor();

        var result = await Controller(db, http).GetByBrn(brn);

        return result.Value
               ?? throw new InvalidOperationException(
                   $"Looking up '{brn}' did not return a record: {result.Result}");
    }

    private static BirthRecordsController Controller(
        NcbrsDbContext db, HttpContext http, int windowDays = 90)
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
                    NullLogger<DuplicateDetectionService>.Instance,
                    districts),
                districts,
                Options.Create(new StatutoryRegistrationOptions { WindowDays = windowDays })),
            new AmendmentService(
                db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current, districts),
            current,
            districts,
            Options.Create(new StatutoryRegistrationOptions { WindowDays = windowDays }))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
