using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the offline outbox upload. The behaviour that matters is
/// per-record isolation: a village post that spent two weeks offline must
/// not lose forty-nine good registrations because the fiftieth is malformed.
/// </summary>
public class SyncBatchTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid OtherFacilityId = Guid.Parse("0199a1b2-0002-7000-8000-000000000002");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public SyncBatchTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        db.Facilities.AddRange(
            new Facility
            {
                FacilityId = FacilityId,
                Name = "Kabwe Village Health Post",
                DistrictId = "D-CENTRAL-07",
                BrnBlockStart = 100_000,
                BrnBlockEnd = 199_999,
                BrnBlockNextAvailable = 100_000
            },
            new Facility
            {
                FacilityId = OtherFacilityId,
                Name = "Lusaka Central Hospital",
                DistrictId = "D-LUSAKA-01",
                BrnBlockStart = 200_000,
                BrnBlockEnd = 299_999,
                BrnBlockNextAvailable = 200_000
            });

        // Enrolled, because a device that may sync is now a thing the centre
        // knows about rather than a string the caller types (WS-B9). The
        // refusal paths have their own tests.
        db.Devices.Add(new Device
        {
            DeviceId = "TABLET-07",
            FacilityId = FacilityId,
            PublicKeyPem = DeviceTestKeys.PublicKeyPem,
            EnrolledByRegistrarId = RegistrarId
        });

        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId,
            FacilityId = FacilityId,
            ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Banda",
            CredentialHash = "dev-placeholder"
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private static SyncController CreateController(NcbrsDbContext db, NoOpEventPublisher? publisher = null)
    {
        var http = AuthTestContext.HttpContextFor();
        var currentRegistrar = AuthTestContext.RegistrarService(db, http);
        publisher ??= new NoOpEventPublisher();

        return new SyncController(
            db,
            new BirthRegistrationService(db, publisher, currentRegistrar,
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance, new DistrictLookup(db)),
                new DistrictLookup(db),
                Options.Create(new StatutoryRegistrationOptions())),
            currentRegistrar,
            publisher,
            new ProvisionalRecordReconciler(db, new DistrictLookup(db)),
            // Enrolment enforced, signatures not: these tests exercise batch
            // processing, and a signature needs a real request body. The
            // signature itself is covered in DeviceEnrolmentTests.
            new DeviceEnrolmentService(db, new DeviceEnrolmentOptions { RequireSignature = false }),
            new RegisterBirthRequestValidator(),
            new DistrictLookup(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static RegisterBirthRequest Record(
        string brn,
        string childFullName = "Chipo Mwale",
        Guid? facilityId = null,
        int? birthWeightGrams = 3200,
        BirthPlurality plurality = BirthPlurality.Singleton,
        int? birthOrder = 1)
        => new()
        {
            Brn = brn,
            FacilityId = facilityId ?? FacilityId,
            ChildFullName = childFullName,
            DateOfBirth = new DateTime(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc),
            Sex = Sex.Female,
            BirthWeightGrams = birthWeightGrams,
            GestationalAgeWeeks = 39.5m,
            Plurality = plurality,
            BirthOrder = birthOrder,
            DeviceId = "TABLET-07"
        };

    private static ApiRequest<SyncBatchRequest> Batch(params RegisterBirthRequest[] records)
        => BatchOf(records.Select(record => new SyncBirthRecord { Birth = record }).ToArray());

    private static ApiRequest<SyncBatchRequest> BatchOf(params SyncBirthRecord[] entries)
        => new()
        {
            Data = new SyncBatchRequest
            {
                DeviceId = "TABLET-07",
                FacilityId = FacilityId,
                Records = entries
            }
        };

    private async Task<SyncBatchResponse> SubmitAsync(ApiRequest<SyncBatchRequest> batch)
    {
        await using var db = NewDb();
        var result = await CreateController(db).SubmitBatch(batch);
        return Assert.IsType<SyncBatchResponse>(result.Value);
    }

    [Fact]
    public async Task AnAllValidBatch_RegistersEveryRecord()
    {
        var response = await SubmitAsync(Batch(
            Record("100001"), Record("100002"), Record("100003")));

        Assert.Equal(3, response.Submitted);
        Assert.Equal(3, response.Registered);
        Assert.Equal(0, response.Rejected);
        Assert.Equal(SyncBatchStatus.Reconciled, response.Status);

        await using var db = NewDb();
        Assert.Equal(3, await db.BirthRecords.CountAsync());
    }

    /// <summary>
    /// The core promise of per-record sync: one bad entry costs only itself.
    /// </summary>
    [Fact]
    public async Task OneMalformedRecord_DoesNotCostTheBatchItsGoodRecords()
    {
        var response = await SubmitAsync(Batch(
            Record("100001"),
            Record("100002", childFullName: ""),          // invalid
            Record("100003", birthWeightGrams: 5),        // invalid
            Record("100004")));

        Assert.Equal(2, response.Registered);
        Assert.Equal(2, response.Rejected);
        Assert.Equal(SyncBatchStatus.Reconciled, response.Status);

        await using var db = NewDb();
        var stored = await db.BirthRecords.Select(record => record.Brn).ToListAsync();
        Assert.Equal(["100001", "100004"], stored.Order());
    }

    [Fact]
    public async Task ARejectedRecord_ReportsWhichFieldsFailed()
    {
        var response = await SubmitAsync(Batch(Record("100002", childFullName: "")));

        var outcome = Assert.Single(response.Records);
        Assert.Equal(SyncRecordStatus.Rejected, outcome.Status);
        Assert.Equal("100002", outcome.Brn);
        Assert.Contains(outcome.Errors!, error => error.Field == "childFullName");
    }

    /// <summary>
    /// A device that never saw the response for its last upload re-sends it.
    /// Already-held records are reported as duplicates, not failures.
    /// </summary>
    [Fact]
    public async Task ReUploadingAnAlreadySyncedBatch_ReportsDuplicatesNotErrors()
    {
        var batch = Batch(Record("100001"), Record("100002"));

        await SubmitAsync(batch);
        var second = await SubmitAsync(batch);

        Assert.Equal(0, second.Registered);
        Assert.Equal(2, second.Duplicates);
        Assert.Equal(0, second.Rejected);
        Assert.Equal(SyncBatchStatus.Reconciled, second.Status);

        await using var db = NewDb();
        Assert.Equal(2, await db.BirthRecords.CountAsync());
    }

    /// <summary>
    /// A record naming a different facility than the batch it arrived in
    /// means the device mixed up its outbox -- filing it would record the
    /// birth against the wrong facility.
    /// </summary>
    [Fact]
    public async Task ARecordForAnotherFacility_IsRejected()
    {
        var response = await SubmitAsync(Batch(
            Record("100001"),
            Record("100002", facilityId: OtherFacilityId)));

        Assert.Equal(1, response.Registered);
        Assert.Equal(1, response.Rejected);

        var rejected = response.Records.Single(outcome => outcome.Status == SyncRecordStatus.Rejected);
        Assert.Contains(rejected.Errors!, error => error.Field == "facilityId");
    }

    [Fact]
    public async Task ABatchWhereNothingLands_IsMarkedFailed()
    {
        var response = await SubmitAsync(Batch(
            Record("100001", childFullName: ""),
            Record("100002", birthWeightGrams: 1)));

        Assert.Equal(0, response.Registered);
        Assert.Equal(2, response.Rejected);
        Assert.Equal(SyncBatchStatus.Failed, response.Status);
    }

    [Fact]
    public async Task TheBatchIsRecorded_WithItsDeviceAndCount()
    {
        await SubmitAsync(Batch(Record("100001"), Record("100002")));

        await using var db = NewDb();
        var batch = await db.SyncBatches.SingleAsync();

        Assert.Equal("TABLET-07", batch.DeviceId);
        Assert.Equal(FacilityId, batch.FacilityId);
        Assert.Equal(2, batch.RecordCount);
        Assert.Equal(SyncBatchStatus.Reconciled, batch.Status);
    }

    [Fact]
    public async Task AnUnknownFacility_RejectsTheWholeBatch()
    {
        await using var db = NewDb();

        var batch = new ApiRequest<SyncBatchRequest>
        {
            Data = new SyncBatchRequest
            {
                DeviceId = "TABLET-07",
                FacilityId = Guid.CreateVersion7(),
                Records = [new SyncBirthRecord { Birth = Record("100001") }]
            }
        };

        var result = await CreateController(db).SubmitBatch(batch);

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, error.StatusCode);
    }

    /// <summary>
    /// Every record that lands is audited individually, so the trail shows
    /// which births a given sync actually created.
    /// </summary>
    [Fact]
    public async Task EachRegisteredRecord_IsAudited()
    {
        await SubmitAsync(Batch(Record("100001"), Record("100002", childFullName: "")));

        await using var db = NewDb();
        var creates = await db.AuditLogs
            .Where(log => log.EntityType == nameof(BirthRecord) && log.Action == "Create")
            .ToListAsync();

        Assert.Single(creates);
        Assert.Equal("100001", creates[0].EntityId);
    }
    // --- per-record attribution ----------------------------------------

    private static readonly Guid ColleagueId = Guid.Parse("0199a1b2-1005-7000-8000-000000000005");
    private static readonly Guid OtherFacilityRegistrarId = Guid.Parse("0199a1b2-1006-7000-8000-000000000006");

    /// <summary>
    /// A colleague at the same facility with an offline PIN -- someone who
    /// could genuinely have unlocked the shared tablet.
    /// </summary>
    private async Task AddColleaguesAsync()
    {
        await using var db = NewDb();

        db.Registrars.AddRange(
            new Registrar
            {
                RegistrarId = ColleagueId,
                FacilityId = FacilityId,
                DisplayName = "Nurse B. Phiri",
                CredentialHash = "pbkdf2-sha256$210000$c2FsdA==$aGFzaA=="
            },
            new Registrar
            {
                RegistrarId = OtherFacilityRegistrarId,
                FacilityId = OtherFacilityId,
                DisplayName = "Dr. Elsewhere",
                CredentialHash = "pbkdf2-sha256$210000$c2FsdA==$aGFzaA=="
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The point of the feature: a shared tablet credits each birth to the
    /// nurse who actually entered it, not to whoever happened to sync.
    /// </summary>
    [Fact]
    public async Task ARecordClaimingAColleague_IsCreditedToThatColleague()
    {
        await AddColleaguesAsync();

        var response = await SubmitAsync(BatchOf(
            new SyncBirthRecord { RegisteredByRegistrarId = ColleagueId, Birth = Record("100001") }));

        Assert.Equal(1, response.Registered);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync(r => r.Brn == "100001");

        Assert.Equal(ColleagueId, record.RegisteredByRegistrarId);
        Assert.NotEqual(RegistrarId, record.RegisteredByRegistrarId);
    }

    [Fact]
    public async Task ARecordWithNoClaimedAuthor_FallsBackToTheUploader()
    {
        var response = await SubmitAsync(Batch(Record("100001")));

        Assert.Equal(1, response.Registered);

        await using var db = NewDb();
        Assert.Equal(RegistrarId, (await db.BirthRecords.SingleAsync()).RegisteredByRegistrarId);
    }

    /// <summary>
    /// Bounds what a compromised device can assert: it cannot credit a birth
    /// to staff at some other facility.
    /// </summary>
    [Fact]
    public async Task ARecordClaimingSomeoneAtAnotherFacility_IsRejected()
    {
        await AddColleaguesAsync();

        var response = await SubmitAsync(BatchOf(
            new SyncBirthRecord { RegisteredByRegistrarId = OtherFacilityRegistrarId, Birth = Record("100001") }));

        Assert.Equal(1, response.Rejected);
        Assert.Contains(response.Records[0].Errors!,
            error => error.Field == "registeredByRegistrarId");
    }

    [Fact]
    public async Task ARecordClaimingAnUnknownRegistrar_IsRejected()
    {
        var response = await SubmitAsync(BatchOf(
            new SyncBirthRecord { RegisteredByRegistrarId = Guid.CreateVersion7(), Birth = Record("100001") }));

        Assert.Equal(1, response.Rejected);
    }

    /// <summary>
    /// Only someone who can actually unlock a device offline can plausibly
    /// have authored an offline registration -- so no PIN means no claim.
    /// </summary>
    [Fact]
    public async Task ARecordClaimingSomeoneWithNoOfflinePin_IsRejected()
    {
        var noPinId = Guid.Parse("0199a1b2-1007-7000-8000-000000000007");

        await using (var db = NewDb())
        {
            db.Registrars.Add(new Registrar
            {
                RegistrarId = noPinId,
                FacilityId = FacilityId,
                DisplayName = "Never Issued A PIN",
                CredentialHash = null
            });
            await db.SaveChangesAsync();
        }

        var response = await SubmitAsync(BatchOf(
            new SyncBirthRecord { RegisteredByRegistrarId = noPinId, Birth = Record("100001") }));

        Assert.Equal(1, response.Rejected);
        Assert.Contains(response.Records[0].Errors!, error => error.Message.Contains("offline PIN"));
    }

    /// <summary>
    /// A bad attribution costs only its own record, like any other per-record
    /// failure.
    /// </summary>
    [Fact]
    public async Task ABadAttribution_DoesNotCostTheBatchItsGoodRecords()
    {
        await AddColleaguesAsync();

        var response = await SubmitAsync(BatchOf(
            new SyncBirthRecord { RegisteredByRegistrarId = ColleagueId, Birth = Record("100001") },
            new SyncBirthRecord { RegisteredByRegistrarId = OtherFacilityRegistrarId, Birth = Record("100002") },
            new SyncBirthRecord { Birth = Record("100003") }));

        Assert.Equal(2, response.Registered);
        Assert.Equal(1, response.Rejected);
    }

    /// <summary>
    /// Uploader and author are kept separately, so it stays visible which of
    /// the two a given attribution rests on.
    /// </summary>
    [Fact]
    public async Task TheBatchRecordsWhoUploadedIt_SeparatelyFromWhoAuthored()
    {
        await AddColleaguesAsync();

        await SubmitAsync(BatchOf(
            new SyncBirthRecord { RegisteredByRegistrarId = ColleagueId, Birth = Record("100001") }));

        await using var db = NewDb();

        Assert.Equal(RegistrarId, (await db.SyncBatches.SingleAsync()).UploadedByRegistrarId);
        Assert.Equal(ColleagueId, (await db.BirthRecords.SingleAsync()).RegisteredByRegistrarId);
    }

    [Fact]
    public async Task TheAuditEntry_NamesTheAuthorNotTheUploader()
    {
        await AddColleaguesAsync();

        await SubmitAsync(BatchOf(
            new SyncBirthRecord { RegisteredByRegistrarId = ColleagueId, Birth = Record("100001") }));

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(
            log => log.EntityType == nameof(BirthRecord) && log.Action == "Create");

        Assert.Equal(ColleagueId, audit.UserId);
    }

    // --- the sync.audit stream --------------------------------------------

    private async Task<(SyncBatchResponse Response, NoOpEventPublisher Publisher)> SubmitWithPublisherAsync(
        ApiRequest<SyncBatchRequest> batch)
    {
        await using var db = NewDb();
        var publisher = new NoOpEventPublisher();
        var result = await CreateController(db, publisher).SubmitBatch(batch);

        return (Assert.IsType<SyncBatchResponse>(result.Value), publisher);
    }

    /// <summary>
    /// Chain of custody for the offline tier: which device delivered what,
    /// and what became of it.
    /// </summary>
    [Fact]
    public async Task AProcessedBatch_IsPublishedToTheAuditStream()
    {
        var (response, publisher) = await SubmitWithPublisherAsync(Batch(
            Record("100001"),
            Record("100002", childFullName: "")));

        var published = Assert.Single(publisher.SyncBatches);

        Assert.Equal(response.SyncBatchId, published.SyncBatchId);
        Assert.Equal("TABLET-07", published.DeviceId);
        Assert.Equal(FacilityId, published.FacilityId);
        Assert.Equal("D-CENTRAL-07", published.DistrictId);
        Assert.Equal(RegistrarId, published.UploadedByRegistrarId);
        Assert.Equal(2, published.Submitted);
        Assert.Equal(1, published.Registered);
        Assert.Equal(1, published.Rejected);
        Assert.Equal(nameof(SyncBatchStatus.Reconciled), published.Status);
    }

    /// <summary>
    /// A district that stops hearing from a village post needs to notice, so
    /// the key is the district rather than the device.
    /// </summary>
    [Fact]
    public async Task TheAuditEvent_IsPartitionedByDistrict()
    {
        var (_, publisher) = await SubmitWithPublisherAsync(Batch(Record("100001")));

        Assert.Contains(("sync-audit", "D-CENTRAL-07"), publisher.Enqueued);
    }

    /// <summary>
    /// A batch where nothing could be accepted is still a delivery that
    /// happened -- and the one a district most needs to see.
    /// </summary>
    [Fact]
    public async Task ABatchWhereNothingWasAccepted_IsStillAudited()
    {
        var (_, publisher) = await SubmitWithPublisherAsync(Batch(Record("100001", childFullName: "")));

        var published = Assert.Single(publisher.SyncBatches);

        Assert.Equal(nameof(SyncBatchStatus.Failed), published.Status);
        Assert.Equal(1, published.Rejected);
    }

    // --- BRN confirmation -------------------------------------------------

    /// <summary>
    /// What sync exists to bring back: the device learns its provisional
    /// number is now permanent (draft 5.1).
    ///
    /// The fixture's facility has never been granted a block, so the numbers
    /// these records carry were issued by nobody -- exactly the state the
    /// reconciler must refuse to confirm. Granting one first is what makes
    /// them legitimate.
    /// </summary>
    [Fact]
    public async Task SyncingARecordFromAGrantedBlock_ConfirmsItsBrn()
    {
        await using (var grant = NewDb())
        {
            var facility = await grant.Facilities.SingleAsync(f => f.FacilityId == FacilityId);
            facility.BrnBlockNextAvailable = 100_200;
            await grant.SaveChangesAsync();
        }

        var response = await SubmitAsync(Batch(Record("100001")));

        var outcome = Assert.Single(response.Records);
        Assert.Equal(SyncRecordStatus.Registered, outcome.Status);
        Assert.True(outcome.BrnConfirmed);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal(RecordStatus.Confirmed, record.Status);
        Assert.Equal(response.SyncBatchId, record.ConfirmedBySyncBatchId);
    }

    /// <summary>
    /// A number the registry never handed out is registered but not
    /// confirmed, and the device is told which -- so it knows to keep showing
    /// the record as provisional rather than treating sync as settling it.
    /// </summary>
    [Fact]
    public async Task SyncingARecordWithAnUnallocatedBrn_RegistersButDoesNotConfirm()
    {
        var response = await SubmitAsync(Batch(Record("100001")));

        var outcome = Assert.Single(response.Records);

        Assert.Equal(SyncRecordStatus.Registered, outcome.Status);
        Assert.False(outcome.BrnConfirmed);
        Assert.Contains("has not been allocated", outcome.BrnConfirmationDetail);

        await using var db = NewDb();
        Assert.Equal(RecordStatus.Provisional, (await db.BirthRecords.SingleAsync()).Status);
    }

    /// <summary>
    /// The exhaustion fallback, end to end (draft 6.3). A post that ran out
    /// of numbers offline syncs, and the centre hands back the real BRN the
    /// device must now show in place of its provisional slip.
    /// </summary>
    [Fact]
    public async Task SyncingAProvisionalRecord_AssignsARealBrnAndReportsIt()
    {
        await using (var grant = NewDb())
        {
            var facility = await grant.Facilities.SingleAsync(f => f.FacilityId == FacilityId);
            facility.BrnBlockNextAvailable = 100_200;
            await grant.SaveChangesAsync();
        }

        var response = await SubmitAsync(Batch(Record("PROV-TABLET07-3")));

        var outcome = Assert.Single(response.Records);

        Assert.Equal(SyncRecordStatus.Registered, outcome.Status);
        Assert.True(outcome.BrnConfirmed);
        Assert.Equal("100200", outcome.AssignedBrn);
        Assert.Contains("was reconciled to", outcome.BrnConfirmationDetail);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal("100200", record.Brn);
        Assert.Equal("PROV-TABLET07-3", record.ProvisionalIdentifier);
        Assert.Equal(RecordStatus.Confirmed, record.Status);
    }

    /// <summary>
    /// A record that rolls back clears the change tracker, which detaches the
    /// batch row with it. Before this was handled, the response said
    /// Reconciled while the stored row sat at Processing forever -- a
    /// divergence between the audit stream and the register itself.
    /// </summary>
    [Fact]
    public async Task TheStoredBatchStatus_MatchesWhatWasReported()
    {
        var response = await SubmitAsync(Batch(
            Record("100001"),
            Record("100002", childFullName: "")));

        await using var db = NewDb();
        var stored = await db.SyncBatches.SingleAsync();

        Assert.Equal(response.Status, stored.Status);
        Assert.NotEqual(SyncBatchStatus.Processing, stored.Status);
    }

    /// <summary>
    /// The capture time survives the offline path, which is the only path
    /// where it differs from the arrival time.
    ///
    /// It reaches the register through the same BirthRegistrationService the
    /// online endpoint uses, so in principle it cannot drift -- but "in
    /// principle" is the assumption worth checking here rather than asserting,
    /// because this is the tier the whole column exists for.
    /// </summary>
    [Fact]
    public async Task ASyncedRecord_KeepsTheTimeTheDeviceRegisteredIt()
    {
        var capturedAt = new DateTime(2026, 9, 11, 6, 15, 0, DateTimeKind.Utc);

        var response = await SubmitAsync(Batch(
            Record("100001") with { RegisteredAtUtc = capturedAt }));

        Assert.Equal(SyncBatchStatus.Reconciled, response.Status);

        await using var db = NewDb();
        var stored = await db.BirthRecords.SingleAsync(record => record.Brn == "100001");

        Assert.Equal(capturedAt, stored.RegisteredAtUtc);

        // Not the arrival time, which is what the register kept before and
        // what it would have fallen back to if the value had been dropped
        // anywhere between the batch and the row.
        Assert.NotEqual(stored.CreatedAtUtc, stored.RegisteredAtUtc);
    }
}
