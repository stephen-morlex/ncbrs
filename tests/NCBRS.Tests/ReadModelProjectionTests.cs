using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Models;
using NCBRS.Consumer.Services;
using NCBRS.Events;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the reporting projection and its behaviour under replay (WS-A5).
///
/// Outbox delivery is at-least-once from two independent directions -- a
/// relay that published but crashed before marking the row dispatched, and
/// Kafka redelivering on rebalance or deliberate replay. Replay is not a
/// fault to be engineered away: it is what the draft keeps 30-90 days of
/// retention for, so a consumer that was offline can catch up.
///
/// The exit criterion is therefore blunt: replaying a partition must leave
/// the totals unchanged. These pin that, and pin the reason it holds --
/// facts keyed by BRN rather than counters, which cannot drift no matter how
/// many times an event arrives.
/// </summary>
public class ReadModelProjectionTests : IDisposable
{
    private const string RegisteredTopic = "ncbrs.birth-records.registered";
    private const string AmendedTopic = "ncbrs.birth-records.amended";
    private const string AnnulledTopic = "ncbrs.birth-records.annulled";

    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ReadModelDbContext> _options;

    public ReadModelProjectionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ReadModelDbContext>().UseSqlite(_connection).Options;

        using var db = new ReadModelDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ReadModelDbContext NewDb() => new(_options);

    private async Task<ProjectionResult> ApplyAsync(string topic, Guid? eventId, object evt)
    {
        await using var db = NewDb();

        return await new BirthRecordProjector(db, NullLogger<BirthRecordProjector>.Instance)
            .ApplyAsync(topic, eventId, JsonSerializer.Serialize(evt));
    }

    private static BirthRegisteredEvent Registered(
        string brn = "100001",
        string districtId = "D-CENTRAL-07",
        string sex = "Female")
        => new(
            brn,
            Guid.CreateVersion7(),
            FacilityId,
            districtId,
            new DateTime(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc),
            sex,
            DateTime.UtcNow,
            Guid.CreateVersion7());

    private async Task<int> LiveBirthsAsync(string districtId = "D-CENTRAL-07")
    {
        await using var db = NewDb();

        // Derived, never stored. There is no counter to double.
        return await db.RegistrationFacts
            .CountAsync(fact => fact.DistrictId == districtId && fact.AnnulledAtUtc == null);
    }

    // --- the exit criterion -------------------------------------------------

    /// <summary>
    /// The plan's exit criterion, stated directly: replaying a partition
    /// leaves the totals unchanged.
    /// </summary>
    [Fact]
    public async Task ReplayingAPartition_LeavesTotalsUnchanged()
    {
        var events = new List<(Guid Id, BirthRegisteredEvent Event)>
        {
            (Guid.CreateVersion7(), Registered("100001")),
            (Guid.CreateVersion7(), Registered("100002")),
            (Guid.CreateVersion7(), Registered("100003"))
        };

        foreach (var (id, evt) in events)
        {
            await ApplyAsync(RegisteredTopic, id, evt);
        }

        Assert.Equal(3, await LiveBirthsAsync());

        // The whole partition again, exactly as a rebalance or a deliberate
        // replay from retention would deliver it.
        foreach (var (id, evt) in events)
        {
            await ApplyAsync(RegisteredTopic, id, evt);
        }

        Assert.Equal(3, await LiveBirthsAsync());
    }

    /// <summary>
    /// The projection is idempotent by construction, so it survives replay
    /// even with no ledger to consult. That matters: a ledger is one more
    /// thing that can be wrong, and totals should not depend on it.
    /// </summary>
    [Fact]
    public async Task EvenWithNoEventId_ReplayDoesNotDoubleCount()
    {
        var evt = Registered("100001");

        await ApplyAsync(RegisteredTopic, null, evt);
        await ApplyAsync(RegisteredTopic, null, evt);
        await ApplyAsync(RegisteredTopic, null, evt);

        Assert.Equal(1, await LiveBirthsAsync());
    }

    [Fact]
    public async Task ARedeliveredEvent_IsRecognisedAsAlreadySeen()
    {
        var id = Guid.CreateVersion7();
        var evt = Registered();

        Assert.Equal(ProjectionOutcome.Applied, (await ApplyAsync(RegisteredTopic, id, evt)).Outcome);
        Assert.Equal(ProjectionOutcome.AlreadySeen, (await ApplyAsync(RegisteredTopic, id, evt)).Outcome);
    }

    /// <summary>
    /// Replay is normal, but a rate of it that changes is the first sign
    /// something upstream is wrong -- and invisible unless recorded.
    /// </summary>
    [Fact]
    public async Task RedeliveriesAreCounted()
    {
        var id = Guid.CreateVersion7();
        var evt = Registered();

        await ApplyAsync(RegisteredTopic, id, evt);
        await ApplyAsync(RegisteredTopic, id, evt);
        await ApplyAsync(RegisteredTopic, id, evt);

        await using var db = NewDb();
        Assert.Equal(3, (await db.ProcessedEvents.SingleAsync()).Deliveries);
    }

    /// <summary>
    /// A replay must rewrite the facts rather than skip them -- skipping
    /// would leave whatever an earlier partial write had got wrong in place.
    /// </summary>
    [Fact]
    public async Task AReplayRewritesTheFact_RatherThanSkippingIt()
    {
        var evt = Registered(sex: "Female");

        await ApplyAsync(RegisteredTopic, null, evt);

        await using (var corrupt = NewDb())
        {
            var fact = await corrupt.RegistrationFacts.SingleAsync();
            fact.Sex = "Wrong";
            fact.DistrictId = "D-WRONG";
            await corrupt.SaveChangesAsync();
        }

        await ApplyAsync(RegisteredTopic, null, evt);

        await using var db = NewDb();
        var repaired = await db.RegistrationFacts.SingleAsync();

        Assert.Equal("Female", repaired.Sex);
        Assert.Equal("D-CENTRAL-07", repaired.DistrictId);
    }

    // --- what the projection holds -------------------------------------------

    [Fact]
    public async Task ARegistration_BecomesAFact()
    {
        var evt = Registered("100001", "D-LUSAKA-01", "Male");

        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), evt);

        await using var db = NewDb();
        var fact = await db.RegistrationFacts.SingleAsync();

        Assert.Equal("100001", fact.Brn);
        Assert.Equal("D-LUSAKA-01", fact.DistrictId);
        Assert.Equal("Male", fact.Sex);
        Assert.Equal(FacilityId, fact.FacilityId);
        Assert.Null(fact.AnnulledAtUtc);
    }

    [Fact]
    public async Task TotalsAreGroupedByDistrict()
    {
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered("100001", "D-CENTRAL-07"));
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered("100002", "D-CENTRAL-07"));
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered("200001", "D-LUSAKA-01"));

        Assert.Equal(2, await LiveBirthsAsync("D-CENTRAL-07"));
        Assert.Equal(1, await LiveBirthsAsync("D-LUSAKA-01"));
    }

    /// <summary>
    /// A correction the projection holds a field for is applied; the rest is
    /// the register's business, not the reporting side's.
    /// </summary>
    [Fact]
    public async Task AnAmendment_UpdatesTheFieldsTheProjectionHolds()
    {
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered(sex: "Female"));

        var amended = new BirthRecordAmendedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Female", "Male")],
            "Sex recorded incorrectly on the notification form.",
            RegistrarId, true, DateTime.UtcNow, Guid.CreateVersion7());

        await ApplyAsync(AmendedTopic, Guid.CreateVersion7(), amended);

        await using var db = NewDb();
        var fact = await db.RegistrationFacts.SingleAsync();

        Assert.Equal("Male", fact.Sex);
        Assert.NotNull(fact.AmendedAtUtc);
        Assert.True(fact.CertificateRevoked);
    }

    /// <summary>
    /// Voided, not deleted: a district's history should not silently change
    /// shape. Aggregates exclude it instead.
    /// </summary>
    [Fact]
    public async Task AnAnnulment_VoidsTheFactWithoutDeletingIt()
    {
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered());

        Assert.Equal(1, await LiveBirthsAsync());

        var annulled = new BirthRecordAnnulledEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            "RegisteredInError", "Filed against the wrong child during training.",
            null, RegistrarId, true, DateTime.UtcNow, Guid.CreateVersion7());

        await ApplyAsync(AnnulledTopic, Guid.CreateVersion7(), annulled);

        Assert.Equal(0, await LiveBirthsAsync());

        await using var db = NewDb();
        var fact = await db.RegistrationFacts.SingleAsync();

        Assert.NotNull(fact.AnnulledAtUtc);
        Assert.Equal("100001", fact.Brn);
    }

    [Fact]
    public async Task ReplayingAnAnnulment_DoesNotUnvoidOrDoubleAnything()
    {
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered());

        var annulled = new BirthRecordAnnulledEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            "FraudulentRegistration", "Investigation established the birth was fabricated.",
            null, RegistrarId, true, DateTime.UtcNow, Guid.CreateVersion7());

        var id = Guid.CreateVersion7();

        await ApplyAsync(AnnulledTopic, id, annulled);
        await ApplyAsync(AnnulledTopic, id, annulled);

        Assert.Equal(0, await LiveBirthsAsync());

        await using var db = NewDb();
        Assert.Single(await db.RegistrationFacts.ToListAsync());
    }

    /// <summary>
    /// Whole streams get replayed, not single events. The order does not
    /// change where it ends up.
    /// </summary>
    [Fact]
    public async Task ReplayingAMixedStream_ReachesTheSameState()
    {
        var registered = (Id: Guid.CreateVersion7(), Event: (object)Registered());
        var amended = (Id: Guid.CreateVersion7(), Event: (object)new BirthRecordAmendedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Female", "Male")], "Corrected.",
            RegistrarId, false, DateTime.UtcNow, Guid.CreateVersion7()));

        async Task PlayAsync()
        {
            await ApplyAsync(RegisteredTopic, registered.Id, registered.Event);
            await ApplyAsync(AmendedTopic, amended.Id, amended.Event);
        }

        await PlayAsync();
        await PlayAsync();
        await PlayAsync();

        Assert.Equal(1, await LiveBirthsAsync());

        await using var db = NewDb();
        Assert.Equal("Male", (await db.RegistrationFacts.SingleAsync()).Sex);
    }

    // --- the other topics -------------------------------------------------------

    private const string NeonatalTopic = "ncbrs.outcomes.neonatal";
    private const string MaternalTopic = "ncbrs.outcomes.maternal";
    private const string SyncAuditTopic = "ncbrs.sync.audit";

    /// <summary>
    /// Outcomes are keyed by the BRN they belong to, so a redelivered death
    /// overwrites its own row. A second row would be a second death, which
    /// is the kind of error a national mortality figure carries quietly.
    /// </summary>
    [Fact]
    public async Task ReplayingAnOutcome_DoesNotRecordASecondDeath()
    {
        var neonatal = new NeonatalOutcomeRecordedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
            "Neonatal", "P21.0", 8, DateTime.UtcNow, null);

        await ApplyAsync(NeonatalTopic, null, neonatal);
        await ApplyAsync(NeonatalTopic, null, neonatal);
        await ApplyAsync(NeonatalTopic, null, neonatal);

        await using var db = NewDb();
        var fact = await db.NeonatalOutcomeFacts.SingleAsync();

        Assert.Equal("100001", fact.Brn);
        Assert.Equal("Neonatal", fact.IcdPmTiming);
        Assert.Equal(8, fact.DaysAfterBirth);
    }

    [Fact]
    public async Task AMaternalDeathIsProjected()
    {
        var maternal = new MaternalOutcomeRecordedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc),
            "O72.1", 15, DateTime.UtcNow, null);

        Assert.Equal(ProjectionOutcome.Applied,
            (await ApplyAsync(MaternalTopic, Guid.CreateVersion7(), maternal)).Outcome);

        await using var db = NewDb();
        Assert.Equal("O72.1", (await db.MaternalOutcomeFacts.SingleAsync()).IcdMmCauseCode);
    }

    /// <summary>
    /// An outcome is not held when its registration is missing, unlike a
    /// correction. It describes a second vital event that stands on its own,
    /// and a perinatal death should not become invisible because the birth
    /// event has not caught up.
    /// </summary>
    [Fact]
    public async Task AnOutcomeIsRecordedEvenWhenItsRegistrationHasNotArrived()
    {
        var neonatal = new NeonatalOutcomeRecordedEvent(
            "999999", Guid.CreateVersion7(), FacilityId,
            new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
            "Intrapartum", "P20.9", 0, DateTime.UtcNow, null);

        Assert.Equal(ProjectionOutcome.Applied,
            (await ApplyAsync(NeonatalTopic, Guid.CreateVersion7(), neonatal)).Outcome);

        await using var db = NewDb();
        Assert.Single(await db.NeonatalOutcomeFacts.ToListAsync());
        Assert.Empty(await db.PendingEvents.ToListAsync());
    }

    /// <summary>
    /// Keyed by batch id, so a redelivered batch cannot inflate a district's
    /// sync volume or its duplicate count.
    /// </summary>
    [Fact]
    public async Task ReplayingASyncBatch_DoesNotInflateItsCounts()
    {
        var batchId = Guid.CreateVersion7();

        var sync = new SyncBatchProcessedEvent(
            batchId, "TABLET-07", FacilityId, "D-CENTRAL-07", RegistrarId,
            10, 9, 1, 0, "Reconciled", DateTime.UtcNow, null);

        await ApplyAsync(SyncAuditTopic, null, sync);
        await ApplyAsync(SyncAuditTopic, null, sync);

        await using var db = NewDb();
        var facts = await db.SyncBatchFacts.ToListAsync();

        var fact = Assert.Single(facts);

        Assert.Equal(batchId, fact.SyncBatchId);
        Assert.Equal(10, fact.Submitted);
        Assert.Equal(1, fact.Duplicates);
    }

    // --- arrival order --------------------------------------------------------

    /// <summary>
    /// Kafka orders within a partition, not across topics, and these are
    /// three topics. A replay from the earliest offset can deliver the whole
    /// of .amended before the whole of .registered -- so out-of-order arrival
    /// is the normal case on a rebuild, not an anomaly.
    /// </summary>
    [Fact]
    public async Task ACorrectionArrivingBeforeItsRegistration_IsHeld()
    {
        var amended = new BirthRecordAmendedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Female", "Male")], "Corrected.",
            RegistrarId, false, DateTime.UtcNow, Guid.CreateVersion7());

        var result = await ApplyAsync(AmendedTopic, Guid.CreateVersion7(), amended);

        Assert.Equal(ProjectionOutcome.Deferred, result.Outcome);
        Assert.Contains("100001", result.Detail);

        // Held, and not counted as a birth in the meantime.
        Assert.Equal(0, await LiveBirthsAsync());

        await using var db = NewDb();
        Assert.Equal("100001", (await db.PendingEvents.SingleAsync()).Brn);
    }

    /// <summary>
    /// The point of holding it: the registration arrives and the correction
    /// lands, rather than being lost because it was early.
    /// </summary>
    [Fact]
    public async Task AHeldCorrection_IsAppliedWhenTheRegistrationArrives()
    {
        var amended = new BirthRecordAmendedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Female", "Male")], "Corrected.",
            RegistrarId, true, DateTime.UtcNow, Guid.CreateVersion7());

        await ApplyAsync(AmendedTopic, Guid.CreateVersion7(), amended);
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered(sex: "Female"));

        await using var db = NewDb();
        var fact = await db.RegistrationFacts.SingleAsync();

        Assert.Equal("Male", fact.Sex);
        Assert.NotNull(fact.AmendedAtUtc);
        Assert.True(fact.CertificateRevoked);

        // Claimed, so it is no longer waiting on anything.
        Assert.Empty(await db.PendingEvents.ToListAsync());
    }

    /// <summary>
    /// The case that made this necessary. An annulment delivered before its
    /// registration used to be dropped, leaving a voided birth counted as a
    /// live one -- so the same events replayed gave a different total.
    /// </summary>
    [Fact]
    public async Task AnAnnulmentArrivingFirst_StillVoidsTheRegistration()
    {
        var annulled = new BirthRecordAnnulledEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            "RegisteredInError", "Filed against the wrong child.",
            null, RegistrarId, true, DateTime.UtcNow, Guid.CreateVersion7());

        await ApplyAsync(AnnulledTopic, Guid.CreateVersion7(), annulled);
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered());

        Assert.Equal(0, await LiveBirthsAsync());

        await using var db = NewDb();
        Assert.NotNull((await db.RegistrationFacts.SingleAsync()).AnnulledAtUtc);
    }

    /// <summary>
    /// Whichever order the topics are replayed in, the projection lands in
    /// the same place. That is the exit criterion restated for the awkward
    /// case.
    /// </summary>
    [Fact]
    public async Task TheOrderTheTopicsAreReplayedIn_DoesNotChangeTheResult()
    {
        var registered = Registered(sex: "Female");
        var amended = new BirthRecordAmendedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Female", "Male")], "Corrected.",
            RegistrarId, false, DateTime.UtcNow, Guid.CreateVersion7());

        // Registration first.
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), registered);
        await ApplyAsync(AmendedTopic, Guid.CreateVersion7(), amended);

        var inOrder = await ProjectedSexAsync();

        // And again, from scratch, correction first.
        await using (var reset = NewDb())
        {
            reset.RegistrationFacts.RemoveRange(reset.RegistrationFacts);
            reset.ProcessedEvents.RemoveRange(reset.ProcessedEvents);
            reset.PendingEvents.RemoveRange(reset.PendingEvents);
            await reset.SaveChangesAsync();
        }

        await ApplyAsync(AmendedTopic, Guid.CreateVersion7(), amended);
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), registered);

        Assert.Equal(inOrder, await ProjectedSexAsync());
        Assert.Equal("Male", inOrder);
    }

    /// <summary>
    /// Two corrections to one record, delivered backwards, must still settle
    /// on the later one -- so held events are ordered by when they happened,
    /// not by when they turned up.
    /// </summary>
    [Fact]
    public async Task HeldCorrections_AreAppliedInTheOrderTheyHappened()
    {
        var earlier = new BirthRecordAmendedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Female", "Male")], "First correction.",
            RegistrarId, false, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), null);

        var later = new BirthRecordAmendedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Male", "Female")], "Corrected again.",
            RegistrarId, false, new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), null);

        // Delivered newest first.
        await ApplyAsync(AmendedTopic, Guid.CreateVersion7(), later);
        await ApplyAsync(AmendedTopic, Guid.CreateVersion7(), earlier);

        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered(sex: "Female"));

        Assert.Equal("Female", await ProjectedSexAsync());
    }

    /// <summary>
    /// A held event is marked processed, so redelivering it holds one copy
    /// rather than stacking duplicates that would all be applied.
    /// </summary>
    [Fact]
    public async Task ARedeliveredHeldEvent_IsNotHeldTwice()
    {
        var id = Guid.CreateVersion7();
        var amended = new BirthRecordAmendedEvent(
            "100001", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Female", "Male")], "Corrected.",
            RegistrarId, false, DateTime.UtcNow, Guid.CreateVersion7());

        Assert.Equal(ProjectionOutcome.Deferred, (await ApplyAsync(AmendedTopic, id, amended)).Outcome);
        Assert.Equal(ProjectionOutcome.AlreadySeen, (await ApplyAsync(AmendedTopic, id, amended)).Outcome);

        await using var db = NewDb();
        Assert.Single(await db.PendingEvents.ToListAsync());
    }

    /// <summary>
    /// A registration that never arrives leaves the event visible rather
    /// than swept up. That is a real gap and someone has to be able to see
    /// it.
    /// </summary>
    [Fact]
    public async Task AnEventWhoseRegistrationNeverArrives_StaysVisible()
    {
        var amended = new BirthRecordAmendedEvent(
            "999999", Guid.CreateVersion7(), FacilityId,
            [new AmendedField("Sex", "Female", "Male")], "Corrected.",
            RegistrarId, false, DateTime.UtcNow, Guid.CreateVersion7());

        await ApplyAsync(AmendedTopic, Guid.CreateVersion7(), amended);

        // A different birth arrives; it must not claim someone else's event.
        await ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), Registered("100001"));

        await using var db = NewDb();
        var pending = await db.PendingEvents.SingleAsync();

        Assert.Equal("999999", pending.Brn);
    }

    private async Task<string> ProjectedSexAsync()
    {
        await using var db = NewDb();

        return (await db.RegistrationFacts.SingleAsync()).Sex;
    }

    // --- gaps ----------------------------------------------------------------

    [Fact]
    public async Task AnUnknownTopic_IsIgnoredWithoutAffectingTheProjection()
    {
        var result = await ApplyAsync("ncbrs.something.else", Guid.CreateVersion7(), Registered());

        Assert.Equal(ProjectionOutcome.Ignored, result.Outcome);
        Assert.Equal(0, await LiveBirthsAsync());
    }

    /// <summary>
    /// An event that cannot be read must not be recorded as processed, or a
    /// deserialisation bug fixed later could never be replayed past.
    /// </summary>
    [Fact]
    public async Task AnUnreadablePayload_IsNotMarkedProcessed()
    {
        await using var db = NewDb();

        var result = await new BirthRecordProjector(db, NullLogger<BirthRecordProjector>.Instance)
            .ApplyAsync(RegisteredTopic, Guid.CreateVersion7(), "null");

        Assert.Equal(ProjectionOutcome.Ignored, result.Outcome);

        await using var verify = NewDb();
        Assert.Empty(await verify.ProcessedEvents.ToListAsync());
    }
}
