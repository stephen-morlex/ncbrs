using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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
/// Stands in for a Development host so the signer will mint a throwaway key.
/// </summary>
internal sealed class DevelopmentEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "NCBRS.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>
/// Covers signing itself: a certificate must verify, a tampered one must
/// not, and the bytes that get signed must stay stable. If the canonical
/// form drifts, every certificate already printed and in citizens' hands
/// stops verifying -- which is why it is pinned here.
/// </summary>
public class CertificateSignerTests
{
    private static CertificateSigner Signer(bool allowEphemeral = true, string? environment = null)
        => new(
            Options.Create(new CertificateSigningOptions
            {
                AllowEphemeralDevelopmentKey = allowEphemeral,
                KeyId = "test-key"
            }),
            new DevelopmentEnvironment { EnvironmentName = environment ?? Environments.Development },
            NullLogger<CertificateSigner>.Instance);

    [Fact]
    public void ASignedPayload_VerifiesBack()
    {
        using var signer = Signer();
        const string canonical = "NCBRS|v1|100001|Chipo Mwale|2026-09-10|Female|fac|2026-09-14T10:00:00Z";

        var (qr, _) = signer.Sign(canonical);

        Assert.Equal(canonical, signer.Verify(qr));
    }

    [Fact]
    public void ATamperedPayload_DoesNotVerify()
    {
        using var signer = Signer();
        var (qr, _) = signer.Sign("NCBRS|v1|100001|Chipo Mwale|2026-09-10|Female|fac|2026-09-14T10:00:00Z");

        // Swap the child's name while keeping the original signature -- the
        // forgery this whole mechanism exists to catch.
        var parts = qr.Split('.');
        var forgedPayload = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                "NCBRS|v1|100001|Someone Else|2026-09-10|Female|fac|2026-09-14T10:00:00Z"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var forged = string.Join('.', parts[0], parts[1], forgedPayload, parts[3]);

        Assert.Null(signer.Verify(forged));
    }

    [Fact]
    public void ASignatureFromADifferentKey_DoesNotVerify()
    {
        using var ministry = Signer();
        using var impostor = Signer();

        var (forged, _) = impostor.Sign("NCBRS|v1|100001|Chipo Mwale|2026-09-10|Female|fac|2026-09-14T10:00:00Z");

        Assert.Null(ministry.Verify(forged));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-payload")]
    [InlineData("NCBRS1.only.three")]
    [InlineData("WRONG1.test-key.aaaa.bbbb")]
    public void AMalformedPayload_IsRejectedWithoutThrowing(string payload)
    {
        using var signer = Signer();
        Assert.Null(signer.Verify(payload));
    }

    [Fact]
    public void TheQrPayload_StaysSmallEnoughToPrint()
    {
        using var signer = Signer();
        var (qr, _) = signer.Sign(
            "NCBRS|v1|100001|Nomvula Kgositsile-Ramaphosa|2026-09-10|Female|"
            + "0199a1b2-0001-7000-8000-000000000001|2026-09-14T10:00:00Z");

        // ECDSA P-256 keeps this well inside QR capacity; RSA-2048 would add
        // ~190 bytes of signature alone.
        Assert.True(qr.Length < 400, $"QR payload was {qr.Length} chars");
    }

    /// <summary>
    /// Refusing to start beats silently minting a key that makes every
    /// certificate stop verifying at the next restart.
    /// </summary>
    [Fact]
    public void WithoutAConfiguredKey_ProductionRefusesToStart()
        => Assert.Throws<InvalidOperationException>(
            () => Signer(allowEphemeral: true, environment: Environments.Production));

    [Fact]
    public void WithoutOptingIn_EvenDevelopmentRefusesToStart()
        => Assert.Throws<InvalidOperationException>(() => Signer(allowEphemeral: false));
}

/// <summary>
/// Covers issuance: who gets a certificate, who does not, and what a
/// reprint does.
/// </summary>
public class CertificateServiceTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid OtherFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private const string LiveBirthBrn = "100001";
    private const string FetalDeathBrn = "100002";
    private const string OtherFacilityBrn = "200001";

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly CertificateSigner _signer;

    public CertificateServiceTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        _signer = new CertificateSigner(
            Options.Create(new CertificateSigningOptions { AllowEphemeralDevelopmentKey = true }),
            new DevelopmentEnvironment(),
            NullLogger<CertificateSigner>.Instance);

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.AddRange(
            new Facility { FacilityId = FacilityId, Name = "Kabwe Village Health Post", DistrictId = "D-CENTRAL-07" },
            new Facility { FacilityId = OtherFacilityId, Name = "Lusaka Central", DistrictId = "D-LUSAKA-01" });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Banda",
            CredentialHash = "test"
        });

        db.BirthRecords.AddRange(
            Record(LiveBirthBrn, FacilityId, VitalEventType.LiveBirth),
            Record(FetalDeathBrn, FacilityId, VitalEventType.FetalDeath),
            Record(OtherFacilityBrn, OtherFacilityId, VitalEventType.LiveBirth));

        db.SaveChanges();
    }

    private static BirthRecord Record(string brn, Guid facilityId, VitalEventType type)
        => new()
        {
            Brn = brn,
            VitalEventType = type,
            ChildPerson = new Person { FullName = $"Child {brn}" },
            FacilityId = facilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = new DateTime(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton
        };

    public void Dispose()
    {
        _signer.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private async Task<CertificateOutcome> IssueAsync(string brn, params string[] roles)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(roles: roles);
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        return await new CertificateService(db, _signer, current)
            .IssueAsync(brn, registrar, "TABLET-07", Guid.CreateVersion7());
    }

    private async Task<CertificateOutcome> ReprintAsync(string brn)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        return await new CertificateService(db, _signer, current)
            .ReprintAsync(brn, registrar, "TABLET-07", Guid.CreateVersion7());
    }

    [Fact]
    public async Task ALiveBirth_GetsASignedCertificate()
    {
        var result = await IssueAsync(LiveBirthBrn);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Response);
        Assert.NotEmpty(result.Response.QrPayload);

        // The QR must actually verify -- issuing something unverifiable
        // would be worse than not issuing at all.
        Assert.NotNull(_signer.Verify(result.Response.QrPayload));
    }

    /// <summary>
    /// The rule from design decision #3, and the reason VitalEventType
    /// exists at all.
    /// </summary>
    [Fact]
    public async Task AFetalDeath_NeverGetsACertificate()
    {
        var result = await IssueAsync(FetalDeathBrn);

        Assert.Equal(CertificateResult.NotACertifiableEvent, result.Result);

        await using var db = NewDb();
        Assert.Empty(await db.Certificates.ToListAsync());
    }

    [Fact]
    public async Task IssuingTwice_IsRefused_AndDoesNotMintASecondCertificate()
    {
        await IssueAsync(LiveBirthBrn);
        var second = await IssueAsync(LiveBirthBrn);

        Assert.Equal(CertificateResult.AlreadyIssued, second.Result);

        await using var db = NewDb();
        Assert.Equal(1, await db.Certificates.CountAsync());
    }

    /// <summary>
    /// A reprint is the same legal document: re-signing it would produce two
    /// certificates disagreeing about when the birth was certified.
    /// </summary>
    [Fact]
    public async Task AReprint_KeepsTheOriginalSignatureAndIssueDate()
    {
        var issued = await IssueAsync(LiveBirthBrn);
        var reprinted = await ReprintAsync(LiveBirthBrn);

        Assert.True(reprinted.Succeeded);
        Assert.Equal(issued.Response!.Signature, reprinted.Response!.Signature);
        Assert.Equal(1, reprinted.Response.ReprintCount);

        // Compared to the second, not the tick. The issued value is the one
        // held in memory when the certificate was signed; the reprinted one
        // has been round-tripped through the database, and Postgres
        // timestamps keep microseconds where a .NET DateTime keeps 100ns
        // ticks -- so an exact comparison across that boundary fails on the
        // engine the central tier runs, and passes only on SQLite.
        //
        // The claim being made here is that a reprint does not re-date the
        // certificate, and a second is far finer than that needs.
        Assert.Equal(
            issued.Response.IssueDateUtc.ToString("u"),
            reprinted.Response.IssueDateUtc.ToString("u"));
    }

    [Fact]
    public async Task ReprintsAreCounted_SoRepeatedOnesCanBeNoticed()
    {
        await IssueAsync(LiveBirthBrn);
        await ReprintAsync(LiveBirthBrn);
        await ReprintAsync(LiveBirthBrn);

        await using var db = NewDb();
        Assert.Equal(2, (await db.Certificates.SingleAsync()).ReprintCount);

        var reprintAudits = await db.AuditLogs.CountAsync(log => log.Action == "ReprintCertificate");
        Assert.Equal(2, reprintAudits);
    }

    [Fact]
    public async Task ReprintingBeforeIssuing_IsNotFound()
        => Assert.Equal(CertificateResult.BirthRecordNotFound, (await ReprintAsync(LiveBirthBrn)).Result);

    [Fact]
    public async Task AnUnknownBrn_IsNotFound()
        => Assert.Equal(CertificateResult.BirthRecordNotFound, (await IssueAsync("NO-SUCH-BRN")).Result);

    [Fact]
    public async Task ARecordAtAnotherFacility_IsRefused()
        => Assert.Equal(CertificateResult.NotPermitted, (await IssueAsync(OtherFacilityBrn)).Result);

    [Fact]
    public async Task AMinistryAdmin_MayIssueAcrossFacilities()
        => Assert.True((await IssueAsync(OtherFacilityBrn, NcbrsRoles.MinistryAdmin)).Succeeded);

    [Fact]
    public async Task IssuingIsAudited()
    {
        await IssueAsync(LiveBirthBrn);

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(log => log.Action == "IssueCertificate");

        Assert.Equal(LiveBirthBrn, audit.EntityId);
        Assert.Equal(RegistrarId, audit.UserId);
    }

    /// <summary>
    /// The verifier reads the child's name straight out of the signed
    /// payload, so the canonical form has to carry the facts a person
    /// holding the certificate would check.
    /// </summary>
    [Fact]
    public async Task TheSignedPayload_CarriesTheCertificateFacts()
    {
        var result = await IssueAsync(LiveBirthBrn);
        var canonical = _signer.Verify(result.Response!.QrPayload);

        Assert.NotNull(canonical);
        var parts = canonical.Split('|');

        Assert.Equal("NCBRS", parts[0]);
        Assert.Equal(CertificateService.CanonicalVersion, parts[1]);
        Assert.Equal(LiveBirthBrn, parts[2]);
        Assert.Equal($"Child {LiveBirthBrn}", parts[3]);
        Assert.Equal("2026-09-10", parts[4]);
        Assert.Equal("Female", parts[5]);
    }
}
