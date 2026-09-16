using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the matcher's judgement. The case that matters most is the one it
/// must NOT flag: twins share a mother, a date and often a sex, and flagging
/// them would put every multiple birth in the country into a review queue.
/// </summary>
public class DuplicateMatcherTests
{
    private static readonly Guid VillagePost = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid Hospital = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly DateTime Born = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);

    private static readonly DuplicateMatcher Matcher = new();

    private static BirthRecord Record(
        string child,
        string? mother = "Grace Mwale",
        DateTime? dateOfBirth = null,
        Sex sex = Sex.Female,
        Guid? facilityId = null,
        BirthPlurality plurality = BirthPlurality.Singleton,
        int? birthOrder = null)
        => new()
        {
            BirthRecordId = Guid.CreateVersion7(),
            Brn = Guid.NewGuid().ToString()[..8],
            ChildPerson = new Person { FullName = child },
            MotherPerson = mother is null ? null : new Person { FullName = mother },
            DateOfBirth = dateOfBirth ?? Born,
            Sex = sex,
            FacilityId = facilityId ?? VillagePost,
            Plurality = plurality,
            BirthOrder = birthOrder
        };

    /// <summary>
    /// The scenario the feature exists for: a village-post registration and
    /// a later hospital one for the same child.
    /// </summary>
    [Fact]
    public void TheSameBirthAtTwoFacilities_ScoresAboveTheReviewThreshold()
    {
        var assessment = Matcher.Assess(
            Record("Chipo Mwale", facilityId: VillagePost),
            Record("Chipo Mwale", facilityId: Hospital));

        Assert.True(assessment.Score >= DuplicateMatcher.ReviewThreshold,
            $"Scored {assessment.Score}");
    }

    /// <summary>
    /// Twins: same mother, same date, same sex, different children. Flagging
    /// these would be actively harmful.
    /// </summary>
    [Fact]
    public void Twins_AreNeverFlagged()
    {
        var first = Record("Baby A Mwale", plurality: BirthPlurality.Twin, birthOrder: 1);
        var second = Record("Baby B Mwale", plurality: BirthPlurality.Twin, birthOrder: 2);

        Assert.Equal(0, Matcher.Assess(first, second).Score);
    }

    [Fact]
    public void TwinsWithIdenticalNames_AreStillNeverFlagged()
    {
        var first = Record("Baby Mwale", plurality: BirthPlurality.Twin, birthOrder: 1);
        var second = Record("Baby Mwale", plurality: BirthPlurality.Twin, birthOrder: 2);

        Assert.Equal(0, Matcher.Assess(first, second).Score);
    }

    [Fact]
    public void DifferentChildrenOfDifferentMothers_AreNotFlagged()
    {
        var assessment = Matcher.Assess(
            Record("Chipo Mwale", mother: "Grace Mwale"),
            Record("Thabo Phiri", mother: "Joyce Phiri", facilityId: Hospital));

        Assert.True(assessment.Score < DuplicateMatcher.ReviewThreshold,
            $"Scored {assessment.Score}");
    }

    [Fact]
    public void BirthsFarApartInTime_AreNotCompared()
    {
        var assessment = Matcher.Assess(
            Record("Chipo Mwale"),
            Record("Chipo Mwale", dateOfBirth: Born.AddMonths(4), facilityId: Hospital));

        Assert.Equal(0, assessment.Score);
    }

    /// <summary>
    /// A second registration is often taken from a parent's recollection, so
    /// a day or two of drift should not hide a duplicate.
    /// </summary>
    [Fact]
    public void ASmallDateDiscrepancy_StillMatches()
    {
        var assessment = Matcher.Assess(
            Record("Chipo Mwale"),
            Record("Chipo Mwale", dateOfBirth: Born.AddDays(1), facilityId: Hospital));

        Assert.True(assessment.Score >= DuplicateMatcher.ReviewThreshold,
            $"Scored {assessment.Score}");
    }

    /// <summary>Spelling drifts between a rushed village entry and a hospital one.</summary>
    [Fact]
    public void MisspelledNames_StillMatch()
    {
        var assessment = Matcher.Assess(
            Record("Chipo Mwale", mother: "Grace Mwale"),
            Record("Chipo Mwali", mother: "Grace Mwali", facilityId: Hospital));

        Assert.True(assessment.Score >= DuplicateMatcher.ReviewThreshold,
            $"Scored {assessment.Score}");
    }

    [Fact]
    public void ADifferentRecordedSex_ArguesAgainstAMatch()
    {
        var same = Matcher.Assess(
            Record("Chipo Mwale"),
            Record("Chipo Mwale", facilityId: Hospital));

        var differing = Matcher.Assess(
            Record("Chipo Mwale"),
            Record("Chipo Mwale", sex: Sex.Male, facilityId: Hospital));

        Assert.True(differing.Score < same.Score);
    }

    [Fact]
    public void AnUnnamedChild_DoesNotCountAgainstAMatch()
    {
        // Children are often registered before being named; an absent name
        // is uninformative, not evidence of difference.
        var assessment = Matcher.Assess(
            Record("", mother: "Grace Mwale"),
            Record("", mother: "Grace Mwale", facilityId: Hospital));

        Assert.DoesNotContain(assessment.Reasons, reason => reason.StartsWith("Child name"));
    }

    [Fact]
    public void ARecordIsNeverItsOwnDuplicate()
    {
        var record = Record("Chipo Mwale");
        Assert.Equal(0, Matcher.Assess(record, record).Score);
    }

    [Fact]
    public void ReasonsExplainTheDecision_SoAReviewerCanJudge()
    {
        var assessment = Matcher.Assess(
            Record("Chipo Mwale", facilityId: VillagePost),
            Record("Chipo Mwale", facilityId: Hospital));

        Assert.Contains(assessment.Reasons, reason => reason.Contains("Same date of birth"));
        Assert.Contains(assessment.Reasons, reason => reason.Contains("different facility"));
    }
}

/// <summary>
/// Covers detection against the database and the review workflow.
/// </summary>
public class DuplicateDetectionServiceTests : IDisposable
{
    private static readonly Guid VillagePost = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid Hospital = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly DateTime Born = new(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public DuplicateDetectionServiceTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.AddRange(
            new Facility { FacilityId = VillagePost, Name = "Kabwe Village Post", DistrictId = "D-CENTRAL-07" },
            new Facility { FacilityId = Hospital, Name = "Lusaka Central", DistrictId = "D-LUSAKA-01" });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = VillagePost,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Banda",
            CredentialHash = "test"
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private static DuplicateDetectionService Service(NcbrsDbContext db)
        => new(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance);

    private async Task<Guid> AddRecordAsync(
        string brn,
        string child,
        Guid facilityId,
        DateTime? dateOfBirth = null,
        string? mother = "Grace Mwale",
        DateTime? createdAt = null)
    {
        await using var db = NewDb();

        var record = new BirthRecord
        {
            Brn = brn,
            ChildPerson = new Person { FullName = child },
            MotherPerson = mother is null ? null : new Person { FullName = mother },
            DateOfBirth = dateOfBirth ?? Born,
            Sex = Sex.Female,
            FacilityId = facilityId,
            RegisteredByRegistrarId = RegistrarId,
            Plurality = BirthPlurality.Singleton,
            CreatedAtUtc = createdAt ?? DateTime.UtcNow
        };

        db.BirthRecords.Add(record);
        await db.SaveChangesAsync();

        return record.BirthRecordId;
    }

    [Fact]
    public async Task ASecondRegistrationOfTheSameBirth_IsFlagged()
    {
        await AddRecordAsync("100001", "Chipo Mwale", VillagePost);
        var second = await AddRecordAsync("200001", "Chipo Mwale", Hospital);

        await using (var db = NewDb())
        {
            Assert.Equal(1, await Service(db).ScanAsync(second));
        }

        await using var verify = NewDb();
        var candidate = await verify.DuplicateCandidates.SingleAsync();

        Assert.Equal(DuplicateReviewStatus.Pending, candidate.Status);
        Assert.True(candidate.Score >= DuplicateMatcher.ReviewThreshold);
        Assert.NotEmpty(candidate.Reasons);
    }

    /// <summary>
    /// Detection must never undo the registration -- both records stay, and
    /// the flag is only a queue entry.
    /// </summary>
    [Fact]
    public async Task FlaggingDoesNotBlockOrRemoveEitherRegistration()
    {
        await AddRecordAsync("100001", "Chipo Mwale", VillagePost);
        var second = await AddRecordAsync("200001", "Chipo Mwale", Hospital);

        await using (var db = NewDb())
        {
            await Service(db).ScanAsync(second);
        }

        await using var verify = NewDb();
        Assert.Equal(2, await verify.BirthRecords.CountAsync());
        Assert.All(await verify.BirthRecords.ToListAsync(),
            record => Assert.Null(record.SupersededByBirthRecordId));
    }

    [Fact]
    public async Task UnrelatedBirths_AreNotFlagged()
    {
        await AddRecordAsync("100001", "Chipo Mwale", VillagePost, mother: "Grace Mwale");
        var second = await AddRecordAsync("200001", "Thabo Phiri", Hospital, mother: "Joyce Phiri");

        await using var db = NewDb();
        Assert.Equal(0, await Service(db).ScanAsync(second));
    }

    [Fact]
    public async Task ScanningTwice_DoesNotFlagTheSamePairAgain()
    {
        await AddRecordAsync("100001", "Chipo Mwale", VillagePost);
        var second = await AddRecordAsync("200001", "Chipo Mwale", Hospital);

        await using (var db = NewDb())
        {
            await Service(db).ScanAsync(second);
        }

        await using (var db = NewDb())
        {
            Assert.Equal(0, await Service(db).ScanAsync(second));
        }

        await using var verify = NewDb();
        Assert.Equal(1, await verify.DuplicateCandidates.CountAsync());
    }

    /// <summary>
    /// The earlier registration survives -- it is closest to the birth, and
    /// the one a family most likely already holds a certificate for.
    /// </summary>
    [Fact]
    public async Task ConfirmingADuplicate_SupersedesTheLaterRecord()
    {
        var earlier = await AddRecordAsync("100001", "Chipo Mwale", VillagePost,
            createdAt: new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc));
        var later = await AddRecordAsync("200001", "Chipo Mwale", Hospital,
            createdAt: new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc));

        await using (var db = NewDb())
        {
            await Service(db).ScanAsync(later);
        }

        Guid candidateId;
        await using (var db = NewDb())
        {
            candidateId = (await db.DuplicateCandidates.SingleAsync()).DuplicateCandidateId;
        }

        await using (var db = NewDb())
        {
            var reviewer = db.Registrars.Single();
            var result = await Service(db).ReviewAsync(
                candidateId, isDuplicate: true, reviewer, "Same child, verified with the mother.", Guid.CreateVersion7());

            Assert.True(result.Succeeded);
        }

        await using var verify = NewDb();

        var survivingRecord = await verify.BirthRecords.SingleAsync(r => r.BirthRecordId == earlier);
        var supersededRecord = await verify.BirthRecords.SingleAsync(r => r.BirthRecordId == later);

        Assert.Null(survivingRecord.SupersededByBirthRecordId);
        Assert.Equal(earlier, supersededRecord.SupersededByBirthRecordId);

        // Nothing is deleted: the registry is append-only.
        Assert.Equal(2, await verify.BirthRecords.CountAsync());
    }

    [Fact]
    public async Task DismissingACandidate_LeavesBothRecordsStanding()
    {
        await AddRecordAsync("100001", "Chipo Mwale", VillagePost);
        var second = await AddRecordAsync("200001", "Chipo Mwale", Hospital);

        await using (var db = NewDb())
        {
            await Service(db).ScanAsync(second);
        }

        Guid candidateId;
        await using (var db = NewDb())
        {
            candidateId = (await db.DuplicateCandidates.SingleAsync()).DuplicateCandidateId;
        }

        await using (var db = NewDb())
        {
            var reviewer = db.Registrars.Single();
            await Service(db).ReviewAsync(candidateId, isDuplicate: false, reviewer, "Different children.", null);
        }

        await using var verify = NewDb();
        var candidate = await verify.DuplicateCandidates.SingleAsync();

        Assert.Equal(DuplicateReviewStatus.Dismissed, candidate.Status);
        Assert.All(await verify.BirthRecords.ToListAsync(),
            record => Assert.Null(record.SupersededByBirthRecordId));
    }

    [Fact]
    public async Task ReviewingTwice_IsRefused()
    {
        await AddRecordAsync("100001", "Chipo Mwale", VillagePost);
        var second = await AddRecordAsync("200001", "Chipo Mwale", Hospital);

        await using (var db = NewDb())
        {
            await Service(db).ScanAsync(second);
        }

        Guid candidateId;
        await using (var db = NewDb())
        {
            candidateId = (await db.DuplicateCandidates.SingleAsync()).DuplicateCandidateId;
        }

        await using (var db = NewDb())
        {
            await Service(db).ReviewAsync(candidateId, true, db.Registrars.Single(), null, null);
        }

        await using (var db = NewDb())
        {
            var second_ = await Service(db).ReviewAsync(candidateId, false, db.Registrars.Single(), null, null);
            Assert.Equal(DuplicateReviewResult.AlreadyReviewed, second_.Result);
        }
    }

    [Fact]
    public async Task AConfirmedDuplicate_IsAudited()
    {
        await AddRecordAsync("100001", "Chipo Mwale", VillagePost);
        var second = await AddRecordAsync("200001", "Chipo Mwale", Hospital);

        await using (var db = NewDb())
        {
            await Service(db).ScanAsync(second);
        }

        await using (var db = NewDb())
        {
            var candidateId = (await db.DuplicateCandidates.SingleAsync()).DuplicateCandidateId;
            await Service(db).ReviewAsync(candidateId, true, db.Registrars.Single(), "verified", null);
        }

        await using var verify = NewDb();

        Assert.True(await verify.AuditLogs.AnyAsync(log => log.Action == "ConfirmDuplicate"));
        Assert.True(await verify.AuditLogs.AnyAsync(log => log.Action == "SupersededAsDuplicate"));
    }

    /// <summary>
    /// A record already ruled a duplicate should not keep surfacing as a
    /// candidate for later registrations.
    /// </summary>
    [Fact]
    public async Task ASupersededRecord_IsExcludedFromFutureScans()
    {
        var earlier = await AddRecordAsync("100001", "Chipo Mwale", VillagePost,
            createdAt: new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc));
        var later = await AddRecordAsync("200001", "Chipo Mwale", Hospital,
            createdAt: new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc));

        await using (var db = NewDb())
        {
            await Service(db).ScanAsync(later);
            var candidateId = (await db.DuplicateCandidates.SingleAsync()).DuplicateCandidateId;
            await Service(db).ReviewAsync(candidateId, true, db.Registrars.Single(), null, null);
        }

        // A third registration of the same child should now match only the
        // surviving record, not the superseded one.
        var third = await AddRecordAsync("300001", "Chipo Mwale", Hospital);

        await using (var db = NewDb())
        {
            Assert.Equal(1, await Service(db).ScanAsync(third));
        }
    }

    [Fact]
    public async Task ThePendingQueue_IsOrderedByLikelihood()
    {
        await AddRecordAsync("100001", "Chipo Mwale", VillagePost);
        var exact = await AddRecordAsync("200001", "Chipo Mwale", Hospital);
        var fuzzy = await AddRecordAsync("200002", "Chipo Mwali", Hospital, dateOfBirth: Born.AddDays(2));

        await using (var db = NewDb())
        {
            await Service(db).ScanAsync(exact);
            await Service(db).ScanAsync(fuzzy);
        }

        await using var verify = NewDb();
        var pending = await Service(verify).PendingAsync(facilityId: null, new PageRequest());

        Assert.True(pending.Items.Count >= 2);
        Assert.True(pending.Items[0].Score >= pending.Items[^1].Score);
    }
}
