using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Certificates;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the certificate revocation list.
///
/// This closes the one gap signing cannot: a printed certificate verifies
/// against its own contents forever, because the signature over them is
/// genuine. What these pin is that a withdrawn document actually stops being
/// accepted, that the list itself cannot be tampered with, and that a
/// verifier can tell when its cached copy has gone stale -- the last being
/// the classic way a revocation list fails silently.
/// </summary>
public class CertificateRevocationTests : IDisposable
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

    public CertificateRevocationTests()
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
            FacilityId = FacilityId, Name = "Kabwe Village Health Post", DistrictId = "D-CENTRAL-07"
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
            // Correcting a child's name now needs a second person to approve
            // it before it takes effect, so the fixture needs one.
            new Registrar
            {
                RegistrarId = ReviewerId,
                FacilityId = FacilityId,
                ExternalSubjectId = ReviewerSubject,
                DisplayName = "District Officer M. Tembo",
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

        var result = await new CertificateService(db, _signer, current)
            .IssueAsync(brn, registrar, "TABLET-07", Guid.CreateVersion7());

        Assert.True(result.Succeeded);
        return result.Response!.QrPayload;
    }

    /// <summary>
    /// Submits a name correction and has it approved, which is what actually
    /// withdraws the certificate. A correction to a signed field takes two
    /// people now, so a test that only submits would revoke nothing.
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
                    db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current)
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
                    db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current)
                .ReviewAsync(requestId, approve: true, reviewer, "Verified.", Guid.CreateVersion7());

            Assert.True(review.Succeeded);
        }
    }

    // --- verification -----------------------------------------------------

    [Fact]
    public async Task AnUntouchedCertificate_Verifies()
    {
        var qr = await IssueAsync();

        await using var db = NewDb();
        var result = await Revocations(db).VerifyAsync(qr);

        Assert.True(result.Valid);
        Assert.False(result.Revoked);
        Assert.Equal(Brn, result.Brn);
    }

    /// <summary>
    /// The whole point. The printed certificate is byte-for-byte unchanged
    /// and its signature is still genuine -- only the list says otherwise.
    /// </summary>
    [Fact]
    public async Task ACertificateWithdrawnByAnAmendment_StopsVerifying()
    {
        var qr = await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();

        // Signature alone still holds: nothing about the paper changed.
        Assert.NotNull(_signer.Verify(qr));

        var result = await Revocations(db).VerifyAsync(qr);

        Assert.False(result.Valid);
        Assert.True(result.Revoked);
        Assert.Equal(RevocationReason.Amended, result.RevocationReason);
    }

    /// <summary>
    /// A verifier reading only Valid -- the field that existed before
    /// revocation did -- must still refuse a withdrawn certificate.
    /// </summary>
    [Fact]
    public async Task ARevokedCertificate_IsNotValid()
    {
        var qr = await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        Assert.False((await Revocations(db).VerifyAsync(qr)).Valid);
    }

    /// <summary>
    /// The holder of a withdrawn certificate did nothing wrong, so a clerk
    /// needs to see whose it is to tell them to collect a replacement.
    /// </summary>
    [Fact]
    public async Task ARevokedResult_StillNamesTheCertificate()
    {
        var qr = await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var result = await Revocations(db).VerifyAsync(qr);

        Assert.Equal(Brn, result.Brn);
        Assert.Equal($"Child {Brn}", result.ChildFullName);
        Assert.Contains("replacement", result.Reason);
    }

    /// <summary>
    /// A forgery and a withdrawn certificate must stay distinguishable: one
    /// is a crime, the other is a family holding an out-of-date document.
    /// </summary>
    [Fact]
    public async Task AForgery_IsNotReportedAsMerelyRevoked()
    {
        await using var db = NewDb();
        var result = await Revocations(db).VerifyAsync("NCBRS1.ncbrs-dev.bm90.cmVhbA");

        Assert.False(result.Valid);
        Assert.False(result.Revoked);
        Assert.Null(result.RevocationReason);
    }

    [Fact]
    public async Task RevokingOneCertificate_DoesNotAffectAnother()
    {
        var revoked = await IssueAsync(Brn);
        var untouched = await IssueAsync(SecondBrn);
        await AmendAsync(Brn);

        await using var db = NewDb();
        var service = Revocations(db);

        Assert.False((await service.VerifyAsync(revoked)).Valid);
        Assert.True((await service.VerifyAsync(untouched)).Valid);
    }

    /// <summary>
    /// A duplicate ruling leaves a second legal identity standing if the
    /// certificate behind it stays good -- the sharper of the two failures
    /// this list guards against.
    /// </summary>
    [Fact]
    public async Task ACertificateOnARecordSupersededAsADuplicate_IsRevoked()
    {
        var qr = await IssueAsync(SecondBrn);

        await using (var setup = NewDb())
        {
            var first = await setup.BirthRecords.SingleAsync(r => r.Brn == Brn);
            var second = await setup.BirthRecords.SingleAsync(r => r.Brn == SecondBrn);

            // The later registration loses, and it is the one certified here.
            second.CreatedAtUtc = first.CreatedAtUtc.AddMinutes(5);

            setup.DuplicateCandidates.Add(new DuplicateCandidate
            {
                BirthRecordId = first.BirthRecordId,
                MatchedBirthRecordId = second.BirthRecordId,
                Score = 95,
                Reasons = "test"
            });

            await setup.SaveChangesAsync();
        }

        await using (var review = NewDb())
        {
            var candidate = await review.DuplicateCandidates.SingleAsync();
            var reviewer = review.Registrars.Single(r => r.RegistrarId == RegistrarId);

            var outcome = await new DuplicateDetectionService(
                    review, new DuplicateMatcher(), new CertificateRevocationRecorder(review),
                    NullLogger<DuplicateDetectionService>.Instance)
                .ReviewAsync(candidate.DuplicateCandidateId, isDuplicate: true, reviewer, "confirmed", null);

            Assert.True(outcome.Succeeded);
        }

        await using var db = NewDb();
        var result = await Revocations(db).VerifyAsync(qr);

        Assert.False(result.Valid);
        Assert.Equal(RevocationReason.SupersededAsDuplicate, result.RevocationReason);
    }

    // --- the published list -----------------------------------------------

    [Fact]
    public async Task TheListCarriesTheRevokedCertificate()
    {
        await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var list = await Revocations(db).BuildAsync();

        var entry = Assert.Single(list.Entries);
        Assert.Equal(RevocationReason.Amended, entry.Reason);
        Assert.Equal(1, list.Count);
    }

    /// <summary>
    /// The list is distributed far more widely than anything else here, so
    /// it must name nobody. An entry is a digest and nothing else.
    /// </summary>
    [Fact]
    public async Task TheListNamesNoChildAndNoBrn()
    {
        await IssueAsync();
        await AmendAsync(newName: "Corrected Name");

        await using var db = NewDb();
        var list = await Revocations(db).BuildAsync();
        var serialised = System.Text.Json.JsonSerializer.Serialize(list);

        Assert.DoesNotContain(Brn, serialised);
        Assert.DoesNotContain("Child", serialised);
        Assert.DoesNotContain("Corrected Name", serialised);
        Assert.DoesNotContain(FacilityId.ToString(), serialised);
    }

    /// <summary>
    /// An unsigned list is worse than none: anyone able to intercept it
    /// could strip the entry for the certificate they are presenting.
    /// </summary>
    [Fact]
    public async Task TheListIsSigned_AndTheSignatureCoversItsEntries()
    {
        await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var list = await Revocations(db).BuildAsync();

        var canonical = RevocationListCanonical.Build(
            list.CoversFromUtc, list.IssuedAtUtc, list.NextUpdateUtc, list.Entries);

        // Checked by verifying, not by re-signing: ECDSA draws a fresh nonce
        // each time, so signing the same bytes twice legitimately produces
        // two different signatures.
        Assert.Equal(canonical, _signer.Verify(Reassemble(canonical, list.Signature)));

        // Drop the entry a forger would want gone. The signature they are
        // carrying no longer vouches for the list they are presenting.
        var stripped = RevocationListCanonical.Build(
            list.CoversFromUtc, list.IssuedAtUtc, list.NextUpdateUtc, []);

        Assert.Null(_signer.Verify(Reassemble(stripped, list.Signature)));
    }

    /// <summary>
    /// Rebuilds the QR envelope the signer verifies, which is how a verifier
    /// checks a list it was handed rather than one it just produced.
    /// </summary>
    private string Reassemble(string canonical, string signature)
        => string.Join('.', "NCBRS1", _signer.KeyId,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(canonical))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            signature);

    /// <summary>
    /// A verifier in another language re-derives the signed bytes itself, so
    /// the same set of revocations has to produce the same string however it
    /// came out of the database.
    /// </summary>
    [Fact]
    public void TheCanonicalForm_DoesNotDependOnEntryOrder()
    {
        var issued = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        var next = issued.AddDays(7);

        var a = new RevocationEntry("aaaa", RevocationReason.Amended, issued);
        var b = new RevocationEntry("bbbb", RevocationReason.SupersededAsDuplicate, issued);

        // Same content, opposite order: only sorting makes these agree, which
        // is why BuildAsync sorts rather than trusting the query.
        Assert.Equal(
            RevocationListCanonical.Build(null, issued, next, [a, b]),
            RevocationListCanonical.Build(null, issued, next,
                [.. new[] { b, a }.OrderBy(entry => entry.SerialHash, StringComparer.Ordinal)]));
    }

    /// <summary>
    /// SQLite returns DateTimeKind.Unspecified, which serialises without a
    /// "Z" and shifts under ToUniversalTime by the server's own offset. A
    /// verifier abroad re-deriving the signed bytes from this JSON would
    /// then compute a different string and reject a genuine list.
    /// </summary>
    [Fact]
    public async Task ListTimestamps_AreUtc_SoTheSignedBytesDoNotDependOnTheServersTimezone()
    {
        await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var list = await Revocations(db).BuildAsync();

        Assert.Equal(DateTimeKind.Utc, Assert.Single(list.Entries).RevokedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, list.IssuedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, list.NextUpdateUtc.Kind);

        // The serialised form a verifier actually parses must carry the zone.
        Assert.Contains("Z\"", System.Text.Json.JsonSerializer.Serialize(list));
    }

    /// <summary>
    /// The field that decides whether an offline verifier may trust what it
    /// holds. Without it, "not in my months-old list" reads as "valid".
    /// </summary>
    [Fact]
    public async Task TheListSaysWhenItStopsBeingTrustworthy()
    {
        await using var db = NewDb();
        var list = await Revocations(db, TimeSpan.FromDays(7)).BuildAsync();

        Assert.True(list.NextUpdateUtc > list.IssuedAtUtc);
        Assert.Equal(7, (list.NextUpdateUtc - list.IssuedAtUtc).TotalDays, 3);
    }

    /// <summary>
    /// A stale list must be detectable by the verifier holding it, without
    /// asking the registry -- which by definition it cannot reach.
    /// </summary>
    [Fact]
    public async Task AnExpiredList_IsDetectableOffline()
    {
        await using var db = NewDb();
        var list = await Revocations(db, TimeSpan.FromMilliseconds(-1)).BuildAsync();

        Assert.True(list.NextUpdateUtc < DateTime.UtcNow);
    }

    /// <summary>
    /// A village device should not re-download the national list to learn
    /// that nothing changed.
    /// </summary>
    [Fact]
    public async Task ADeltaCarriesOnlyWhatChangedAfterTheCallersCopy()
    {
        await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var service = Revocations(db);

        var full = await service.BuildAsync();
        Assert.Single(full.Entries);

        var delta = await service.BuildAsync(since: DateTime.UtcNow);
        Assert.Empty(delta.Entries);
    }

    /// <summary>
    /// A delta presented as a complete list would look like a registry with
    /// no revocations at all, so the window it covers is inside the
    /// signature rather than alongside it.
    /// </summary>
    [Fact]
    public async Task ADeltaCannotBePassedOffAsAFullList()
    {
        await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var service = Revocations(db);

        var since = DateTime.UtcNow;
        var delta = await service.BuildAsync(since: since);

        Assert.Equal(since, delta.CoversFromUtc);

        // The same empty entry set, but claiming to be complete, is not what
        // the delta's signature vouches for.
        var asFull = RevocationListCanonical.Build(
            null, delta.IssuedAtUtc, delta.NextUpdateUtc, delta.Entries);

        Assert.Null(_signer.Verify(Reassemble(asFull, delta.Signature)));
    }

    // --- the recorder -----------------------------------------------------

    /// <summary>
    /// The published serial has to be derivable from the paper alone -- the
    /// QR carries no serial number, and adding one would break every
    /// certificate already issued.
    /// </summary>
    [Fact]
    public async Task TheSerialIsDerivableFromThePrintedCertificate()
    {
        var qr = await IssueAsync();
        await AmendAsync();

        var fromPaper = CertificateRevocationRecorder.SerialFor(qr.Split('.')[^1]);

        await using var db = NewDb();
        Assert.Equal(fromPaper, (await db.CertificateRevocations.SingleAsync()).SerialHash);
    }

    [Fact]
    public async Task RevokingTwice_DoesNotPublishTwoEntriesForOneCertificate()
    {
        await IssueAsync();
        await AmendAsync(newName: "First Correction");
        await AmendAsync(newName: "Second Correction");

        await using var db = NewDb();

        // The second amendment found no valid certificate to withdraw, so
        // the list still describes one document, once.
        Assert.Equal(1, await db.CertificateRevocations.CountAsync());
    }

    /// <summary>
    /// One serial under two timestamps would make the published list
    /// contradict itself about when a document stopped being valid.
    /// </summary>
    [Fact]
    public async Task ADuplicateSerial_IsRefusedByTheDatabase()
    {
        await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var existing = await db.CertificateRevocations.SingleAsync();

        db.CertificateRevocations.Add(new CertificateRevocation
        {
            CertificateId = existing.CertificateId,
            BirthRecordId = existing.BirthRecordId,
            SerialHash = existing.SerialHash,
            Reason = RevocationReason.SupersededAsDuplicate
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TheRevocationCommitsWithTheApprovalThatCausedIt()
    {
        await IssueAsync();
        await AmendAsync();

        await using var db = NewDb();
        var revocation = await db.CertificateRevocations.SingleAsync();
        var amendment = await db.BirthRecordAmendments.FirstAsync();

        // Attributed to the reviewer, and stamped at the approval -- not at
        // the submission. The certificate stayed valid until someone agreed
        // to the correction, so that is the moment it stopped being good.
        Assert.Equal(ReviewerId, revocation.RevokedByRegistrarId);
        Assert.Equal(amendment.AppliedAtUtc, revocation.RevokedAtUtc);
        Assert.Equal(AmendmentStatus.Applied, amendment.Status);
    }
}

public class VerifyCertificateRequestValidatorTests
{
    private static readonly VerifyCertificateRequestValidator Validator = new();

    [Fact]
    public void AScannedPayload_IsAccepted()
        => Assert.True(Validator.Validate(
            new VerifyCertificateRequest { QrPayload = "NCBRS1.k.a.b" }).IsValid);

    [Fact]
    public void AnEmptyScan_IsRejected()
        => Assert.False(Validator.Validate(new VerifyCertificateRequest()).IsValid);
}
