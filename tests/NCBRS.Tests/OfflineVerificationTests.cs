using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Certificates;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers verification with no connection at all -- the district office or
/// village post the whole system is built around (design decision #1).
///
/// Membership is the easy half. What these mostly pin is the third answer:
/// the cases where the cached list is not entitled to say a certificate is
/// good. An offline verifier that reads "not in my list" as "valid" has
/// re-created the exact hole the list exists to close, and every way the
/// cache can fall short is tested to refuse rather than approve.
/// </summary>
public class OfflineVerificationTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid ReviewerId = Guid.Parse("0199a1b2-1002-7000-8000-000000000002");
    private const string ReviewerSubject = "22222222-2222-4222-8222-222222222222";

    private const string Brn = "100001";
    private const string SecondBrn = "100002";

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;
    private readonly CertificateSigner _signer;
    private readonly CertificatePayloadVerifier _verifier;

    public OfflineVerificationTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        _signer = new CertificateSigner(
            Options.Create(new CertificateSigningOptions { AllowEphemeralDevelopmentKey = true }),
            new DevelopmentEnvironment(),
            NullLogger<CertificateSigner>.Instance);

        // Exactly what a device is provisioned with: the public half, and
        // nothing else.
        _verifier = CertificatePayloadVerifier.FromPem(_signer.PublicKeyPem(), _signer.KeyId);

        using var db = new NcbrsDbContext(_options);
        db.Database.EnsureCreated();

        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId, Name = "Terekeka Village Health Post", DistrictId = "SS-CE-TER"
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
            // A correction to a child's name needs a second person to approve
            // it before the certificate is withdrawn.
            new Registrar
            {
                RegistrarId = ReviewerId,
                FacilityId = FacilityId,
                ExternalSubjectId = ReviewerSubject,
                DisplayName = "District Officer M. Kenyi",
                Role = RegistrarRole.DistrictOfficer
            });

        db.BirthRecords.AddRange(Record(Brn), Record(SecondBrn));
        db.SaveChanges();
    }

    private static BirthRecord Record(string brn)
        => new()
        {
            Brn = brn,
            VitalEventType = VitalEventType.LiveBirth,
            ChildPerson = new Person { FullName = $"Child {brn}" },
            FacilityId = FacilityId,
            RegisteredByRegistrarId = RegistrarId,
            DateOfBirth = new DateTime(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            Status = RecordStatus.Confirmed
        };

    public void Dispose()
    {
        _verifier.Dispose();
        _signer.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private CertificateRevocationService Revocations(NcbrsDbContext db, TimeSpan? validFor = null)
        => new(db, _signer, Options.Create(new CertificateRevocationOptions
        {
            ValidFor = validFor ?? TimeSpan.FromDays(7)
        }));

    private async Task<string> IssueAsync(string brn = Brn)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var result = await new CertificateService(db, _signer, current, new CountyLookup(db))
            .IssueAsync(brn, registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.True(result.Succeeded);
        return result.Response!.QrPayload;
    }

    /// <summary>
    /// Submits a name correction and has it approved. Approval is what
    /// withdraws the certificate, so a test that only submitted would revoke
    /// nothing and prove nothing.
    /// </summary>
    private async Task AmendAsync(string brn = Brn, string newName = "Corrected Name")
    {
        Guid requestId;

        await using (var db = NewDb())
        {
            var http = AuthTestContext.HttpContextFor();
            var current = AuthTestContext.RegistrarService(db, http);
            var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

            var outcome = await new AmendmentService(
                    db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current,
                    new CountyLookup(db))
                .AmendAsync(brn, new AmendBirthRecordRequest
                {
                    ChildFullName = newName,
                    Reason = "Name misspelled on the original form.",
                    DeviceId = "TABLET-07"
                }, registrar, Guid.CreateVersion7());

            Assert.True(outcome.Succeeded);
            requestId = outcome.Response!.AmendmentRequestId;
        }

        await using (var db = NewDb())
        {
            var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);
            var current = AuthTestContext.RegistrarService(db, http);
            var reviewer = db.Registrars.Single(r => r.RegistrarId == ReviewerId);

            var review = await new AmendmentService(
                    db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current,
                    new CountyLookup(db))
                .ReviewAsync(requestId, approve: true, reviewer, "Verified.", Guid.CreateVersion7());

            Assert.True(review.Succeeded);
        }
    }

    private async Task<CertificateRevocationList> FetchAsync(
        DateTime? since = null, TimeSpan? validFor = null)
    {
        await using var db = NewDb();
        return await Revocations(db, validFor).BuildAsync(since);
    }

    private OfflineCertificateVerifier Device(
        DateTime? nowUtc = null,
        params CertificateRevocationList[] cached)
        => new(_verifier, RevocationListCache.Load(cached, _verifier, nowUtc ?? DateTime.UtcNow));

    // --- the ordinary cases -----------------------------------------------

    [Fact]
    public async Task AGoodCertificate_IsAcceptedWithNoNetwork()
    {
        var qr = await IssueAsync();
        var list = await FetchAsync();

        var result = Device(cached: list).Verify(qr);

        Assert.Equal(OfflineVerdict.Valid, result.Verdict);
        Assert.True(result.Accept);
        Assert.Equal(Brn, result.Brn);
    }

    /// <summary>
    /// The point of the exercise: the paper is byte-for-byte unchanged and
    /// its signature still holds, but the device knows better.
    /// </summary>
    [Fact]
    public async Task AWithdrawnCertificate_IsCaughtOffline()
    {
        var qr = await IssueAsync();
        await AmendAsync();
        var list = await FetchAsync();

        Assert.NotNull(_verifier.Verify(qr));

        var result = Device(cached: list).Verify(qr);

        Assert.Equal(OfflineVerdict.Revoked, result.Verdict);
        Assert.False(result.Accept);
        Assert.Equal(RevocationReason.Amended, result.RevocationReason);
        Assert.Contains("replacement", result.Detail);
    }

    [Fact]
    public async Task AForgery_IsRefusedWithoutDetail()
    {
        var list = await FetchAsync();
        var result = Device(cached: list).Verify("NCBRS1.ncbrs-dev.bm90.cmVhbA");

        Assert.Equal(OfflineVerdict.NotGenuine, result.Verdict);
        Assert.Null(result.Brn);
    }

    /// <summary>
    /// A device provisioned with one key must not report every certificate
    /// signed by a newer one as a forgery without that being diagnosable.
    /// </summary>
    [Fact]
    public async Task ACertificateSignedByAnotherKey_IsNotGenuineHere()
    {
        var qr = await IssueAsync();

        using var otherSigner = new CertificateSigner(
            Options.Create(new CertificateSigningOptions
            {
                AllowEphemeralDevelopmentKey = true, KeyId = "other-key"
            }),
            new DevelopmentEnvironment(),
            NullLogger<CertificateSigner>.Instance);

        using var otherVerifier = CertificatePayloadVerifier.FromPem(
            otherSigner.PublicKeyPem(), otherSigner.KeyId);

        var list = await FetchAsync();
        var device = new OfflineCertificateVerifier(
            otherVerifier, RevocationListCache.Load([], otherVerifier, DateTime.UtcNow));

        Assert.Equal(OfflineVerdict.NotGenuine, device.Verify(qr).Verdict);
    }

    [Fact]
    public async Task RevokingOne_DoesNotAffectAnother()
    {
        var revoked = await IssueAsync(Brn);
        var untouched = await IssueAsync(SecondBrn);
        await AmendAsync(Brn);

        var device = Device(cached: await FetchAsync());

        Assert.Equal(OfflineVerdict.Revoked, device.Verify(revoked).Verdict);
        Assert.Equal(OfflineVerdict.Valid, device.Verify(untouched).Verdict);
    }

    // --- when the cache may not answer ------------------------------------

    /// <summary>
    /// The classic way a revocation list fails silently. A months-old cache
    /// contains no entry for a certificate revoked last week, and reading
    /// that absence as "valid" is exactly the forgery a holder wants
    /// accepted.
    /// </summary>
    [Fact]
    public async Task AnExpiredCache_AnswersUnknownRatherThanValid()
    {
        var qr = await IssueAsync();
        var list = await FetchAsync();

        var result = Device(nowUtc: list.NextUpdateUtc.AddSeconds(1), cached: list).Verify(qr);

        Assert.Equal(OfflineVerdict.Unknown, result.Verdict);
        Assert.False(result.Accept);
        Assert.Contains("connected office", result.Detail);
    }

    /// <summary>
    /// Expiry withdraws the right to say "good", not the knowledge already
    /// held: an entry a stale list does contain is still a revocation, and
    /// answering Unknown there would send someone away to be told the same
    /// thing by a connected office.
    /// </summary>
    [Fact]
    public async Task AnExpiredCache_StillReportsWhatItDoesKnow()
    {
        var qr = await IssueAsync();
        await AmendAsync();
        var list = await FetchAsync();

        var result = Device(nowUtc: list.NextUpdateUtc.AddDays(30), cached: list).Verify(qr);

        Assert.Equal(OfflineVerdict.Revoked, result.Verdict);
    }

    [Fact]
    public async Task ADeviceWithNoListAtAll_AnswersUnknown()
    {
        var qr = await IssueAsync();

        var result = Device().Verify(qr);

        Assert.Equal(OfflineVerdict.Unknown, result.Verdict);
        Assert.Equal(RevocationCoverage.Empty,
            RevocationListCache.Load([], _verifier, DateTime.UtcNow).Coverage);
    }

    /// <summary>
    /// A delta says only what changed inside its window. On its own it
    /// establishes nothing about what was revoked before it, so absence
    /// from it proves nothing.
    /// </summary>
    [Fact]
    public async Task ADeltaAlone_CannotConfirmACertificate()
    {
        var qr = await IssueAsync();
        var delta = await FetchAsync(since: DateTime.UtcNow.AddMinutes(-1));

        Assert.NotNull(delta.CoversFromUtc);

        var result = Device(cached: delta).Verify(qr);

        Assert.Equal(OfflineVerdict.Unknown, result.Verdict);
        Assert.Contains("full list", result.Detail);
    }

    /// <summary>
    /// The case a device actually lives in: a full list fetched once, then
    /// deltas. It may confirm certificates as long as the deltas chain on
    /// without a gap.
    /// </summary>
    [Fact]
    public async Task AFullListPlusContiguousDeltas_CanConfirm()
    {
        var qr = await IssueAsync();
        var full = await FetchAsync();

        await AmendAsync(SecondBrn);
        var delta = await FetchAsync(since: full.IssuedAtUtc);

        var cache = RevocationListCache.Load([full, delta], _verifier, DateTime.UtcNow);

        Assert.Equal(RevocationCoverage.Complete, cache.Coverage);
        Assert.Equal(OfflineVerdict.Valid, Device(cached: [full, delta]).Verify(qr).Verdict);
    }

    /// <summary>
    /// A device that skipped a sync has a window nothing accounts for. Any
    /// certificate revoked inside it is missing from the cache, so the cache
    /// must stop claiming completeness rather than quietly approve.
    /// </summary>
    [Fact]
    public async Task AGapBetweenTheFullListAndADelta_ForfeitsCompleteness()
    {
        var qr = await IssueAsync();
        var full = await FetchAsync();

        // Starts an hour after the full list ended: whatever happened in
        // between was never downloaded.
        var delta = await FetchAsync(since: full.IssuedAtUtc.AddHours(1));

        var cache = RevocationListCache.Load([full, delta], _verifier, DateTime.UtcNow);

        Assert.Equal(RevocationCoverage.Incomplete, cache.Coverage);
        Assert.Contains("never downloaded", cache.Detail);
        Assert.Equal(OfflineVerdict.Unknown, Device(cached: [full, delta]).Verify(qr).Verdict);
    }

    /// <summary>
    /// A merged delta still carries entries the full list predates, so the
    /// chain has to answer from the union rather than the newest list alone.
    /// </summary>
    [Fact]
    public async Task ARevocationArrivingInADelta_IsFound()
    {
        var qr = await IssueAsync();
        var full = await FetchAsync();

        await AmendAsync();
        var delta = await FetchAsync(since: full.IssuedAtUtc);

        Assert.Empty(full.Entries);
        Assert.Single(delta.Entries);

        Assert.Equal(OfflineVerdict.Revoked, Device(cached: [full, delta]).Verify(qr).Verdict);
    }

    // --- a tampered cache --------------------------------------------------

    /// <summary>
    /// The cache sits on the device, which is precisely what someone holding
    /// a revoked certificate would edit -- deleting the one entry that names
    /// theirs. The signature is what makes that useless.
    /// </summary>
    [Fact]
    public async Task ACacheWithItsEntryStripped_IsUntrusted()
    {
        var qr = await IssueAsync();
        await AmendAsync();
        var list = await FetchAsync();

        var stripped = list with { Entries = [], Count = 0 };
        var cache = RevocationListCache.Load([stripped], _verifier, DateTime.UtcNow);

        Assert.Equal(RevocationCoverage.Untrusted, cache.Coverage);

        // And so the certificate is not confirmed -- the tamper buys nothing.
        Assert.Equal(OfflineVerdict.Unknown, Device(cached: stripped).Verify(qr).Verdict);
    }

    /// <summary>
    /// Pushing NextUpdateUtc out would let an expired cache keep approving.
    /// It is inside the signature for that reason.
    /// </summary>
    [Fact]
    public async Task ACacheWithItsExpiryExtended_IsUntrusted()
    {
        var list = await FetchAsync();
        var extended = list with { NextUpdateUtc = list.NextUpdateUtc.AddYears(5) };

        Assert.Equal(RevocationCoverage.Untrusted,
            RevocationListCache.Load([extended], _verifier, DateTime.UtcNow).Coverage);
    }

    /// <summary>
    /// Relabelling a delta as complete would turn a narrow window into an
    /// apparently empty national list.
    /// </summary>
    [Fact]
    public async Task ADeltaRelabelledAsComplete_IsUntrusted()
    {
        var delta = await FetchAsync(since: DateTime.UtcNow.AddMinutes(-1));
        var relabelled = delta with { CoversFromUtc = null };

        Assert.Equal(RevocationCoverage.Untrusted,
            RevocationListCache.Load([relabelled], _verifier, DateTime.UtcNow).Coverage);
    }

    /// <summary>
    /// One bad list poisons the view rather than merely being dropped: a
    /// cache that has been edited says nothing reliable about what else is
    /// missing from it.
    /// </summary>
    [Fact]
    public async Task OneTamperedListMakesTheWholeCacheUntrusted()
    {
        await IssueAsync();
        var full = await FetchAsync();

        await AmendAsync();
        var delta = await FetchAsync(since: full.IssuedAtUtc);

        Assert.Empty(full.Entries);
        Assert.Single(delta.Entries);

        // The full list is untouched and genuine; only the delta carrying
        // the inconvenient entry was edited.
        var cache = RevocationListCache.Load(
            [full, delta with { Entries = [], Count = 0 }], _verifier, DateTime.UtcNow);

        Assert.Equal(RevocationCoverage.Untrusted, cache.Coverage);
        Assert.Equal(0, cache.Count);
    }

    // --- the two derivations must agree ------------------------------------

    /// <summary>
    /// The device recomputes the published serial from the printed
    /// signature. If its derivation ever drifted from the recorder's, no
    /// revoked certificate would ever be found -- a failure that is silent
    /// and fails open, so it is pinned here directly.
    /// </summary>
    [Fact]
    public async Task TheDeviceDerivesTheSameSerialAsTheRegistry()
    {
        var qr = await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var published = (await db.CertificateRevocations.SingleAsync()).SerialHash;

        Assert.Equal(published, OfflineCertificateVerifier.SerialFor(qr.Split('.')[^1]));
        Assert.Equal(published, CertificateRevocationRecorder.SerialFor(qr.Split('.')[^1]));
    }

    /// <summary>
    /// Online and offline must not disagree about the same certificate --
    /// two answers from one registry is worse than either being wrong.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlineAndOfflineAgree(bool amend)
    {
        var qr = await IssueAsync();

        if (amend)
        {
            await AmendAsync();
        }

        var list = await FetchAsync();

        await using var db = NewDb();
        var online = await Revocations(db).VerifyAsync(qr);
        var offline = Device(cached: list).Verify(qr);

        Assert.Equal(online.Valid, offline.Accept);
        Assert.Equal(online.Revoked, offline.Verdict is OfflineVerdict.Revoked);
        Assert.Equal(online.RevocationReason, offline.RevocationReason);
    }
}
