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
/// Covers concurrent amendment conflict handling (draft 6.3).
///
/// The case: a health worker corrects a birth weight on a tablet that has
/// been offline for three weeks, while a district officer corrects the same
/// field centrally. Both act in good faith on what is in front of them and
/// neither knows about the other.
///
/// The draft's rule is last-writer-wins with an audit trail, flagged for
/// registrar review rather than auto-merged silently. What these defend is
/// the second half of that: the resolution is mechanical, but the judgement
/// is not, and without a flag nobody would ever learn the other value had
/// existed.
/// </summary>
public class AmendmentConflictTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly Guid ReviewerId = Guid.Parse("0199a1b2-1002-7000-8000-000000000002");
    private const string ReviewerSubject = "22222222-2222-4222-8222-222222222222";

    private const string Brn = "100001";
    private const string Reason = "Scale re-read at the bedside.";

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public AmendmentConflictTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

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
                RegistrarId = ReviewerId,
                FacilityId = FacilityId,
                ExternalSubjectId = ReviewerSubject,
                DisplayName = "District Officer M. Tembo",
                Role = RegistrarRole.DistrictOfficer
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
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private async Task<AmendmentOutcome> AmendAsync(
        AmendBirthRecordRequest request, Guid? asRegistrar = null)
    {
        await using var db = NewDb();
        var registrarId = asRegistrar ?? RegistrarId;
        var subject = registrarId == ReviewerId ? ReviewerSubject : AuthTestContext.DefaultSubject;

        var http = AuthTestContext.HttpContextFor(subject, NcbrsRoles.DistrictOfficer);
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == registrarId);

        return await new AmendmentService(
                db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current,
                new DistrictLookup(db))
            .AmendAsync(Brn, request, registrar, Guid.CreateVersion7());
    }

    private static AmendBirthRecordRequest Weight(int grams, string? observed = null)
        => new()
        {
            BirthWeightGrams = grams,
            Reason = Reason,
            DeviceId = "TABLET-07",
            ObservedValues = observed is null ? null : [new ObservedValue("BirthWeightGrams", observed)]
        };

    private async Task<IReadOnlyList<AmendmentConflictResponse>> ConflictsAsync()
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);

        var page = await new AmendmentService(
                db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db),
                AuthTestContext.RegistrarService(db, http),
                new DistrictLookup(db))
            .ConflictsAsync(null, new PageRequest());

        return page.Items;
    }

    // --- detection ---------------------------------------------------------

    /// <summary>
    /// The central correction landed first; the device's arrives having been
    /// composed against a value the register no longer holds.
    /// </summary>
    [Fact]
    public async Task AChangeComposedAgainstAStaleValue_IsFlagged()
    {
        // The district officer corrects centrally: 3200 -> 3300.
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);

        // The tablet, offline since before that, still believes 3200.
        var outcome = await AmendAsync(Weight(3250, observed: "3200"));

        Assert.True(outcome.Succeeded);

        var conflict = Assert.Single(await ConflictsAsync());

        Assert.Equal("BirthWeightGrams", conflict.Field);
        Assert.Equal("3200", conflict.ExpectedPreviousValue);
        Assert.Equal("3300", conflict.ActualPreviousValue);
        Assert.Equal(Brn, conflict.Brn);
        Assert.Equal("Nurse A. Banda", conflict.SubmittedByRegistrarName);
    }

    /// <summary>
    /// Last-writer-wins: the arriving correction is applied, not refused. The
    /// flag is what makes it reviewable rather than silent.
    /// </summary>
    [Fact]
    public async Task TheArrivingChange_StillWins()
    {
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);
        await AmendAsync(Weight(3250, observed: "3200"));

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal(3250, record.BirthWeightGrams);

        var conflict = await db.AmendmentConflicts.SingleAsync();
        Assert.Equal("3250", conflict.ResolvedValue);
    }

    /// <summary>
    /// The previous value the history records is what the register actually
    /// held, not what the device thought it held -- otherwise the audit trail
    /// would assert a transition that never happened.
    /// </summary>
    [Fact]
    public async Task TheHistory_RecordsTheRealPreviousValue()
    {
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);
        await AmendAsync(Weight(3250, observed: "3200"));

        await using var db = NewDb();
        var latest = await db.BirthRecordAmendments
            .OrderByDescending(amendment => amendment.AmendedAtUtc)
            .FirstAsync();

        Assert.Equal("3300", latest.PreviousValue);
        Assert.Equal("3250", latest.NewValue);
    }

    [Fact]
    public async Task AConflict_IsAudited()
    {
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);
        await AmendAsync(Weight(3250, observed: "3200"));

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(log => log.Action.StartsWith("AmendmentConflict:"));

        Assert.Equal("AmendmentConflict:BirthWeightGrams", audit.Action);
        Assert.Equal(Brn, audit.EntityId);
    }

    // --- what is NOT a conflict --------------------------------------------

    /// <summary>
    /// A device whose view still matches the register is not in conflict with
    /// anything.
    /// </summary>
    [Fact]
    public async Task AChangeComposedAgainstTheCurrentValue_IsNotFlagged()
    {
        await AmendAsync(Weight(3250, observed: "3200"));

        Assert.Empty(await ConflictsAsync());
    }

    /// <summary>
    /// An online caller read the record moments ago and sends no observed
    /// values. Treating silence as a mismatch would flag every ordinary
    /// correction and train registrars to ignore the queue.
    /// </summary>
    [Fact]
    public async Task AnOnlineCallerSendingNoObservedValues_IsNotFlagged()
    {
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);
        await AmendAsync(Weight(3250));

        Assert.Empty(await ConflictsAsync());
    }

    /// <summary>
    /// Conflicts are per field, not per record. A device correcting the birth
    /// weight while the centre corrected a name has clashed with nothing.
    /// </summary>
    [Fact]
    public async Task AChangeToADifferentFieldThanTheCentresIsNotAConflict()
    {
        // Centre corrects the mother's name; that queues for approval.
        await AmendAsync(new AmendBirthRecordRequest
        {
            MotherFullName = "Grace Mwale",
            Reason = "Mother was not recorded at registration.",
            DeviceId = "DO-LAPTOP"
        }, asRegistrar: ReviewerId);

        // The device corrects the weight, and its view of the weight is right.
        await AmendAsync(Weight(3250, observed: "3200"));

        Assert.Empty(await ConflictsAsync());
    }

    /// <summary>
    /// Only fields actually being changed are checked. A device echoing back
    /// its whole view would otherwise raise conflicts on fields it is not
    /// proposing to touch.
    /// </summary>
    [Fact]
    public async Task AStaleObservationOfAFieldNotBeingChanged_IsIgnored()
    {
        var outcome = await AmendAsync(new AmendBirthRecordRequest
        {
            BirthWeightGrams = 3250,
            Reason = Reason,
            DeviceId = "TABLET-07",
            ObservedValues =
            [
                new ObservedValue("BirthWeightGrams", "3200"),
                new ObservedValue("ChildFullName", "Someone Entirely Different")
            ]
        });

        Assert.True(outcome.Succeeded);
        Assert.Empty(await ConflictsAsync());
    }

    // --- the approval track -------------------------------------------------

    /// <summary>
    /// An identity field is flagged at submission but nothing has been
    /// applied, so no winner is claimed. The approval-time check remains the
    /// gate on whether it ever takes effect.
    /// </summary>
    [Fact]
    public async Task AConflictOnAFieldAwaitingApproval_ClaimsNoWinnerYet()
    {
        await AmendAsync(new AmendBirthRecordRequest
        {
            ChildFullName = "Chipo M. Banda",
            Reason = "Surname corrected at the parents request.",
            DeviceId = "TABLET-07",
            ObservedValues = [new ObservedValue("ChildFullName", "Someone Else")]
        });

        var conflict = Assert.Single(await ConflictsAsync());

        Assert.Equal("ChildFullName", conflict.Field);
        Assert.Equal("Someone Else", conflict.ExpectedPreviousValue);
        Assert.Equal("Chipo Mwale", conflict.ActualPreviousValue);
        Assert.Null(conflict.ResolvedValue);

        await using var db = NewDb();
        var record = await db.BirthRecords.Include(r => r.ChildPerson).SingleAsync();
        Assert.Equal("Chipo Mwale", record.ChildPerson!.FullName);
    }

    // --- review -------------------------------------------------------------

    private async Task<AmendmentReviewOutcome> ReviewConflictAsync(Guid conflictId, bool uphold)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor(ReviewerSubject, NcbrsRoles.DistrictOfficer);
        var current = AuthTestContext.RegistrarService(db, http);
        var reviewer = db.Registrars.Single(r => r.RegistrarId == ReviewerId);

        return await new AmendmentService(
                db, new NoOpEventPublisher(), new CertificateRevocationRecorder(db), current,
                new DistrictLookup(db))
            .ReviewConflictAsync(conflictId, uphold, "Checked against the ward register.",
                reviewer, Guid.CreateVersion7());
    }

    [Fact]
    public async Task UpholdingAConflict_ClearsItFromTheQueue()
    {
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);
        await AmendAsync(Weight(3250, observed: "3200"));

        var conflict = Assert.Single(await ConflictsAsync());

        Assert.True((await ReviewConflictAsync(conflict.AmendmentConflictId, uphold: true)).Succeeded);
        Assert.Empty(await ConflictsAsync());

        await using var db = NewDb();
        var reviewed = await db.AmendmentConflicts.SingleAsync();

        Assert.Equal(AmendmentConflictStatus.Upheld, reviewed.Status);
        Assert.Equal(ReviewerId, reviewed.ReviewedByRegistrarId);
        Assert.Contains("ward register", reviewed.ReviewNote);
    }

    [Fact]
    public async Task MarkingAConflictCorrected_RecordsThatJudgement()
    {
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);
        await AmendAsync(Weight(3250, observed: "3200"));

        var conflict = Assert.Single(await ConflictsAsync());
        await ReviewConflictAsync(conflict.AmendmentConflictId, uphold: false);

        await using var db = NewDb();
        Assert.Equal(AmendmentConflictStatus.Corrected,
            (await db.AmendmentConflicts.SingleAsync()).Status);
    }

    /// <summary>
    /// The row is kept whichever way it is judged: that two values existed is
    /// the fact worth keeping, not just which one won.
    /// </summary>
    [Fact]
    public async Task AReviewedConflict_IsRetained()
    {
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);
        await AmendAsync(Weight(3250, observed: "3200"));

        var conflict = Assert.Single(await ConflictsAsync());
        await ReviewConflictAsync(conflict.AmendmentConflictId, uphold: true);

        await using var db = NewDb();
        var retained = await db.AmendmentConflicts.SingleAsync();

        Assert.Equal("3200", retained.ExpectedPreviousValue);
        Assert.Equal("3300", retained.ActualPreviousValue);
        Assert.Equal("3250", retained.ResolvedValue);
    }

    [Fact]
    public async Task ReviewingTwice_IsRefused()
    {
        await AmendAsync(Weight(3300), asRegistrar: ReviewerId);
        await AmendAsync(Weight(3250, observed: "3200"));

        var conflict = Assert.Single(await ConflictsAsync());
        await ReviewConflictAsync(conflict.AmendmentConflictId, uphold: true);

        Assert.Equal(AmendmentReviewResult.AlreadyReviewed,
            (await ReviewConflictAsync(conflict.AmendmentConflictId, uphold: true)).Result);
    }

    [Fact]
    public async Task AnUnknownConflict_IsNotFound()
        => Assert.Equal(AmendmentReviewResult.NotFound,
            (await ReviewConflictAsync(Guid.CreateVersion7(), uphold: true)).Result);
}
