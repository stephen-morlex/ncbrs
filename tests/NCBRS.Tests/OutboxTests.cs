using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Events;
using NCBRS.Kafka;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the property the outbox exists for: an event is staged in the same
/// transaction as the change that produced it, so the two can never disagree.
/// Publishing directly used to mean a rollback announced a registration that
/// never landed, and a crash between commit and publish lost it silently.
/// </summary>
public class OutboxTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private const string District = "SS-CE-TER";

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public OutboxTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId,
            Name = "Terekeka Village Health Post",
            DistrictId = District,
            BrnBlockStart = 100_000,
            BrnBlockEnd = 199_999,
            BrnBlockNextAvailable = 100_000
        });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Lado",
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

    private static IOptions<KafkaOptions> KafkaSettings()
        => Options.Create(new KafkaOptions { BootstrapServers = "localhost:9092" });

    private static BirthRegistrationService Registration(NcbrsDbContext db)
    {
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);

        return new BirthRegistrationService(
            db,
            new OutboxEventPublisher(db, KafkaSettings()),
            current,
            new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance, new CountyLookup(db)),
                new CountyLookup(db),
                Options.Create(new StatutoryRegistrationOptions()));
    }

    private static RegisterBirthRequest Request(string brn)
        => new()
        {
            Brn = brn,
            FacilityId = FacilityId,
            ChildFullName = "Ayen Deng",
            DateOfBirth = new DateTime(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            BirthOrder = 1,
            MotherFullName = "Nyandeng Deng",
            DeviceId = "TABLET-07"
        };

    [Fact]
    public async Task RegisteringABirth_StagesItsEventInTheOutbox()
    {
        await using (var db = NewDb())
        {
            var registrar = db.Registrars.Single();
            var result = await Registration(db).RegisterAsync(Request("100001"), registrar, Guid.CreateVersion7());
            Assert.True(result.Succeeded);
        }

        await using var verify = NewDb();
        var message = await verify.OutboxMessages.SingleAsync();

        Assert.Null(message.DispatchedAtUtc);
        Assert.Equal(0, message.AttemptCount);
        Assert.Contains("100001", message.Payload);
    }

    /// <summary>
    /// Partitioning by district is what the draft calls for (Section 6.4.1).
    /// A null key round-robins, which would scatter one district's events
    /// across partitions and lose their relative ordering.
    /// </summary>
    [Fact]
    public async Task TheStagedEvent_IsKeyedByDistrict()
    {
        await using (var db = NewDb())
        {
            var registrar = db.Registrars.Single();
            await Registration(db).RegisterAsync(Request("100001"), registrar, Guid.CreateVersion7());
        }

        await using var verify = NewDb();
        Assert.Equal(District, (await verify.OutboxMessages.SingleAsync()).PartitionKey);
    }

    [Fact]
    public async Task TheStagedEvent_CarriesTheTransactionId()
    {
        var transactionId = Guid.CreateVersion7();

        await using (var db = NewDb())
        {
            var registrar = db.Registrars.Single();
            await Registration(db).RegisterAsync(Request("100001"), registrar, transactionId);
        }

        await using var verify = NewDb();
        Assert.Equal(transactionId, (await verify.OutboxMessages.SingleAsync()).TransactionId);
    }

    /// <summary>
    /// The whole point: roll the transaction back and the event goes with it.
    /// Under the old design the event had already gone to Kafka by now, and
    /// consumers had been told about a birth that does not exist.
    /// </summary>
    [Fact]
    public async Task RollingBackTheTransaction_DiscardsTheEventToo()
    {
        await using (var db = NewDb())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            var registrar = db.Registrars.Single();
            var result = await Registration(db).RegisterAsync(Request("100001"), registrar, Guid.CreateVersion7());
            Assert.True(result.Succeeded);

            await transaction.RollbackAsync();
        }

        await using var verify = NewDb();

        // Neither survives -- they were one unit.
        Assert.Empty(await verify.BirthRecords.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ARegistrationThatFails_StagesNoEvent()
    {
        await using var db = NewDb();
        var registrar = db.Registrars.Single();

        var request = Request("100001") with { FacilityId = Guid.CreateVersion7() };
        var result = await Registration(db).RegisterAsync(request, registrar, Guid.CreateVersion7());

        Assert.False(result.Succeeded);

        await using var verify = NewDb();
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    /// <summary>
    /// Staging is pure database work, so a broker outage cannot reach a
    /// registration at all. This used to depend on the publisher being
    /// carefully fire-and-forget; it is now structural.
    /// </summary>
    [Fact]
    public async Task StagingTouchesNoBroker_SoAnOutageCannotDelayARegistration()
    {
        await using var db = NewDb();
        var registrar = db.Registrars.Single();

        // Points at a port with nothing on it. If staging did any network
        // I/O this would stall; it returns immediately because it does not.
        var publisher = new OutboxEventPublisher(
            db, Options.Create(new KafkaOptions { BootstrapServers = "localhost:9" }));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        publisher.EnqueueBirthRegistered(
            new BirthRegisteredEvent("100001", Guid.CreateVersion7(), FacilityId, District,
                DateTime.UtcNow, "Female", DateTime.UtcNow, Guid.CreateVersion7()),
            District);

        await db.SaveChangesAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Staging took {stopwatch.Elapsed.TotalSeconds:F1}s");

        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task EachEventType_GoesToItsOwnTopic()
    {
        await using var db = NewDb();
        var publisher = new OutboxEventPublisher(db, KafkaSettings());
        var options = KafkaSettings().Value;

        publisher.EnqueueBirthRegistered(
            new BirthRegisteredEvent("1", Guid.CreateVersion7(), FacilityId, District,
                DateTime.UtcNow, "Female", DateTime.UtcNow, null), District);

        publisher.EnqueueNeonatalOutcome(
            new NeonatalOutcomeRecordedEvent("1", Guid.CreateVersion7(), FacilityId,
                DateTime.UtcNow, "Neonatal", "N3", 5, DateTime.UtcNow, null), District);

        publisher.EnqueueMaternalOutcome(
            new MaternalOutcomeRecordedEvent("1", Guid.CreateVersion7(), FacilityId,
                DateTime.UtcNow, "M2", 10, DateTime.UtcNow, null), District);

        await db.SaveChangesAsync();

        var topics = await db.OutboxMessages.Select(message => message.Topic).ToListAsync();

        Assert.Contains(options.BirthRecordsRegisteredTopic, topics);
        Assert.Contains(options.NeonatalOutcomesTopic, topics);
        Assert.Contains(options.MaternalOutcomesTopic, topics);
    }
}
