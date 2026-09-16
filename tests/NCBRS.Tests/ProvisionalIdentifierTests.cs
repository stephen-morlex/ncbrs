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

public class ProvisionalIdentifierFormatTests
{
    [Theory]
    [InlineData("PROV-TABLET07-3")]
    [InlineData("PROV-TABLET-07-3")]
    [InlineData("PROV-chw.phone_12-4001")]
    public void AWellFormedIdentifier_IsRecognised(string value)
    {
        Assert.True(ProvisionalIdentifier.Looks(value));
        Assert.True(ProvisionalIdentifier.IsWellFormed(value));
    }

    [Theory]
    [InlineData("PROV-")]
    [InlineData("PROV-TABLET07")]
    [InlineData("PROV-TABLET07-")]
    [InlineData("PROV-TABLET07-abc")]
    [InlineData("PROV-TABLET 07-3")]
    public void AMalformedIdentifier_LooksProvisionalButIsRejected(string value)
    {
        // Both halves matter: it is recognised as an attempt at a provisional
        // identifier, so it is not treated as a BRN -- and then refused,
        // rather than sitting in the register as neither.
        Assert.True(ProvisionalIdentifier.Looks(value));
        Assert.False(ProvisionalIdentifier.IsWellFormed(value));
    }

    [Theory]
    [InlineData("100001")]
    [InlineData("")]
    [InlineData(null)]
    public void ARealBrn_IsNotMistakenForOne(string? value)
        => Assert.False(ProvisionalIdentifier.Looks(value));

    [Fact]
    public void TheFormat_CarriesTheDeviceSoTwoPostsCannotCollide()
    {
        Assert.NotEqual(
            ProvisionalIdentifier.For("TABLET-07", 3),
            ProvisionalIdentifier.For("TABLET-08", 3));
    }
}

/// <summary>
/// Covers the BRN block exhaustion fallback (draft 6.3).
///
/// The case: a village post offline for weeks runs out of the numbers it was
/// granted. It cannot ask for more and it cannot turn families away, so it
/// issues a clearly-flagged provisional identifier and the centre assigns a
/// real BRN when the record arrives.
///
/// What these defend is that the flag is never lost. A provisional
/// identifier must not be mistaken for a BRN, must not be certified over,
/// must still resolve after it is replaced, and the replacement must be
/// recorded rather than quietly swapped in.
/// </summary>
public class ProvisionalRecordReconcilerTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly CertificateSigner _signer;

    public ProvisionalRecordReconcilerTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        _signer = new CertificateSigner(
            Options.Create(new CertificateSigningOptions { AllowEphemeralDevelopmentKey = true }),
            new DevelopmentEnvironment(),
            NullLogger<CertificateSigner>.Instance);

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        // A block of 200 has been granted; 100200 onward is still the
        // registry's to give.
        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId,
            Name = "Kabwe Village Health Post",
            DistrictId = "D-CENTRAL-07",
            BrnBlockStart = 100_000,
            BrnBlockNextAvailable = 100_200,
            BrnBlockEnd = 199_999
        });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Banda",
            CredentialHash = "test"
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

    private static RegisterBirthRequest Request(string identifier)
        => new()
        {
            Brn = identifier,
            FacilityId = FacilityId,
            ChildFullName = "Chipo Mwale",
            DateOfBirth = DateTime.UtcNow.Date.AddDays(-4),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            BirthOrder = 1,
            DeviceId = "TABLET-07"
        };

    private async Task<RegistrationResult> RegisterAsync(string identifier)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        return await new BirthRegistrationService(
                db, new NoOpEventPublisher(), current,
                new DuplicateDetectionService(db, new DuplicateMatcher(),
                    new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance,
                    new DistrictLookup(db)),
                Options.Create(new StatutoryRegistrationOptions()))
            .RegisterAsync(Request(identifier), registrar, Guid.CreateVersion7());
    }

    private async Task<ProvisionalReconciliationResult> ReconcileAsync(string identifier)
    {
        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync(r => r.ProvisionalIdentifier == identifier);

        return await new ProvisionalRecordReconciler(db, new DistrictLookup(db))
            .ReconcileAsync(record, RegistrarId, Guid.CreateVersion7());
    }

    // --- registration under a provisional identifier ------------------------

    /// <summary>
    /// The birth is registered. A post that stops registering when its block
    /// runs out sends families away from the only health worker they may see
    /// for weeks.
    /// </summary>
    [Fact]
    public async Task ARecordUnderAProvisionalIdentifier_IsRegistered()
    {
        var result = await RegisterAsync("PROV-TABLET07-3");

        Assert.True(result.Succeeded);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal("PROV-TABLET07-3", record.Brn);
        Assert.Equal("PROV-TABLET07-3", record.ProvisionalIdentifier);
        Assert.Null(record.ReconciledAtUtc);
    }

    /// <summary>
    /// It was never drawn from a block, so there is nothing to confirm it
    /// against -- and it must not be reported as a number the registry
    /// vouches for.
    /// </summary>
    [Fact]
    public async Task AProvisionalIdentifier_IsNeverConfirmedAsABrn()
    {
        var result = await RegisterAsync("PROV-TABLET07-3");

        Assert.False(result.BrnReconciliation!.Confirmed);

        await using var db = NewDb();
        Assert.Equal(RecordStatus.Provisional, (await db.BirthRecords.SingleAsync()).Status);
    }

    /// <summary>
    /// An exhausted block is an expected consequence of a long offline
    /// stretch, not a device inventing numbers it was never granted. The two
    /// must stay distinguishable in the audit trail.
    /// </summary>
    [Fact]
    public async Task AProvisionalIdentifier_IsAuditedDistinctlyFromAnInventedBrn()
    {
        await RegisterAsync("PROV-TABLET07-3");

        await using var db = NewDb();
        // Filtered by action, not just id: the ordinary Create row carries
        // the same identifier.
        var audit = await db.AuditLogs.SingleAsync(
            log => log.Action == "ProvisionalIdentifierAccepted");

        Assert.Equal("PROV-TABLET07-3", audit.EntityId);
    }

    // --- reconciliation -----------------------------------------------------

    [Fact]
    public async Task ReconcilingAssignsTheNextRealBrn()
    {
        await RegisterAsync("PROV-TABLET07-3");

        var result = await ReconcileAsync("PROV-TABLET07-3");

        Assert.True(result.Assigned);
        Assert.Equal("100200", result.AssignedBrn);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal("100200", record.Brn);
        Assert.Equal(RecordStatus.Confirmed, record.Status);
        Assert.NotNull(record.ReconciledAtUtc);
        Assert.NotNull(record.ConfirmedAtUtc);
    }

    /// <summary>
    /// A family may be holding the slip the device printed. Losing the
    /// identifier would leave a clerk handed it months later with nothing to
    /// search on.
    /// </summary>
    [Fact]
    public async Task TheProvisionalIdentifier_SurvivesReconciliation()
    {
        await RegisterAsync("PROV-TABLET07-3");
        await ReconcileAsync("PROV-TABLET07-3");

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal("PROV-TABLET07-3", record.ProvisionalIdentifier);
        Assert.NotEqual(record.ProvisionalIdentifier, record.Brn);
    }

    /// <summary>
    /// "Reconciled, not silently merged" (draft 6.3): the act is recorded,
    /// and the audit row names the identifier that was replaced.
    /// </summary>
    [Fact]
    public async Task ReconciliationIsAudited_NamingWhatItReplaced()
    {
        await RegisterAsync("PROV-TABLET07-3");
        await ReconcileAsync("PROV-TABLET07-3");

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(log => log.Action.StartsWith("ReconcileProvisional"));

        Assert.Equal("ReconcileProvisional:PROV-TABLET07-3", audit.Action);
        Assert.Equal("100200", audit.EntityId);
    }

    /// <summary>
    /// Numbers come from the same counter that grants device blocks, so a
    /// reconciled record can never be given one a device is already holding.
    /// </summary>
    [Fact]
    public async Task ReconciliationConsumesFromTheSameCounterAsBlockGrants()
    {
        await RegisterAsync("PROV-TABLET07-3");
        await ReconcileAsync("PROV-TABLET07-3");

        await using var db = NewDb();
        Assert.Equal(100_201, (await db.Facilities.SingleAsync()).BrnBlockNextAvailable);
    }

    [Fact]
    public async Task TwoProvisionalRecords_GetDifferentBrns()
    {
        await RegisterAsync("PROV-TABLET07-3");
        await RegisterAsync("PROV-TABLET07-4");

        var first = await ReconcileAsync("PROV-TABLET07-3");
        var second = await ReconcileAsync("PROV-TABLET07-4");

        Assert.Equal("100200", first.AssignedBrn);
        Assert.Equal("100201", second.AssignedBrn);
    }

    [Fact]
    public async Task ReconcilingARecordThatAlreadyHasABrn_DoesNothing()
    {
        await RegisterAsync("PROV-TABLET07-3");
        await ReconcileAsync("PROV-TABLET07-3");

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        var again = await new ProvisionalRecordReconciler(db, new DistrictLookup(db))
            .ReconcileAsync(record, RegistrarId, Guid.CreateVersion7());

        Assert.Equal(ProvisionalReconciliation.NotProvisional, again.Outcome);
        Assert.Equal("100200", record.Brn);
    }

    /// <summary>
    /// If the facility's own pre-approved range is used up centrally there is
    /// no number to give. The record keeps its provisional identifier rather
    /// than being refused -- extending the range is a central act the record
    /// must wait for, not a reason to lose the birth.
    /// </summary>
    [Fact]
    public async Task WhenTheFacilityRangeIsExhausted_TheRecordKeepsItsIdentifier()
    {
        await RegisterAsync("PROV-TABLET07-3");

        await using (var exhaust = NewDb())
        {
            var facility = await exhaust.Facilities.SingleAsync();
            facility.BrnBlockNextAvailable = facility.BrnBlockEnd + 1;
            await exhaust.SaveChangesAsync();
        }

        var result = await ReconcileAsync("PROV-TABLET07-3");

        Assert.Equal(ProvisionalReconciliation.FacilityRangeExhausted, result.Outcome);
        Assert.Contains("exhausted its pre-approved BRN range", result.Detail);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal("PROV-TABLET07-3", record.Brn);
        Assert.Null(record.ReconciledAtUtc);
    }

    // --- the certificate ----------------------------------------------------

    /// <summary>
    /// Signing over a provisional identifier would produce a certificate
    /// whose subject the register stops knowing by that name the moment it
    /// reconciles.
    /// </summary>
    [Fact]
    public async Task NoCertificateIsIssued_WhileTheIdentifierIsProvisional()
    {
        await RegisterAsync("PROV-TABLET07-3");

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current, new DistrictLookup(db))
            .IssueAsync("PROV-TABLET07-3", registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.Equal(CertificateResult.AwaitingBrnReconciliation, result.Result);
    }

    [Fact]
    public async Task OnceReconciled_TheCertificateIsIssuedAgainstTheRealBrn()
    {
        await RegisterAsync("PROV-TABLET07-3");
        await ReconcileAsync("PROV-TABLET07-3");

        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current, new DistrictLookup(db))
            .IssueAsync("100200", registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.True(result.Succeeded);

        // The signed payload carries the real number, never the provisional.
        var payload = _signer.Verify(result.Response!.QrPayload);

        Assert.Contains("100200", payload);
        Assert.DoesNotContain("PROV-", payload);
    }
}

public class ProvisionalIdentifierValidationTests
{
    private static readonly RegisterBirthRequestValidator Validator = new();

    private static RegisterBirthRequest Request(string brn) => new()
    {
        Brn = brn,
        FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001"),
        ChildFullName = "Chipo Mwale",
        DateOfBirth = DateTime.UtcNow.Date.AddDays(-4),
        Sex = Sex.Female,
        Plurality = BirthPlurality.Singleton,
        BirthOrder = 1,
        DeviceId = "TABLET-07"
    };

    [Fact]
    public void AWellFormedProvisionalIdentifier_IsAccepted()
        => Assert.True(Validator.Validate(Request("PROV-TABLET07-3")).IsValid);

    /// <summary>
    /// A malformed one is neither a BRN the centre can reconcile nor a
    /// fallback it can recognise, so it must not enter the register at all.
    /// </summary>
    [Fact]
    public void AMalformedProvisionalIdentifier_IsRejected()
    {
        var result = Validator.Validate(Request("PROV-TABLET07"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage.Contains("PROV-{deviceId}-{sequence}"));
    }

    [Fact]
    public void AnOrdinaryBrn_IsUnaffectedByTheProvisionalRule()
        => Assert.True(Validator.Validate(Request("100001")).IsValid);
}
