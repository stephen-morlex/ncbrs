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
/// Covers the reconciler on its own: given a facility's block state, is this
/// number one the registry actually handed out?
/// </summary>
public class BrnReconcilerTests
{
    private static Facility Facility(long start = 100_000, long next = 100_200, long end = 199_999)
        => new()
        {
            FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001"),
            Name = "Terekeka Village Health Post",
            DistrictId = "SS-CE-TER",
            BrnBlockStart = start,
            BrnBlockNextAvailable = next,
            BrnBlockEnd = end
        };

    [Theory]
    [InlineData("100000")]
    [InlineData("100001")]
    [InlineData("100199")]
    public void ANumberFromAGrantedBlock_Confirms(string brn)
        => Assert.True(BrnReconciler.Reconcile(brn, Facility()).Confirmed);

    /// <summary>
    /// BrnBlockNextAvailable is the first number not yet handed to any
    /// device, so the boundary itself belongs to nobody.
    /// </summary>
    [Theory]
    [InlineData("100200")]
    [InlineData("100201")]
    [InlineData("150000")]
    public void ANumberTheRegistryNeverHandedOut_IsNotConfirmed(string brn)
    {
        var result = BrnReconciler.Reconcile(brn, Facility());

        Assert.Equal(BrnReconciliation.NotYetAllocated, result.Outcome);
        Assert.Contains("issued up to 100199", result.Detail);
    }

    /// <summary>
    /// The collision this whole check exists to prevent: a device inventing
    /// numbers from a range that belongs to some other facility.
    /// </summary>
    [Theory]
    [InlineData("99999")]
    [InlineData("200000")]
    public void ANumberOutsideTheFacilitysRange_IsNotConfirmed(string brn)
        => Assert.Equal(BrnReconciliation.OutsideFacilityRange,
            BrnReconciler.Reconcile(brn, Facility()).Outcome);

    [Theory]
    [InlineData("BRN-100001")]
    [InlineData("")]
    [InlineData("-100001")]
    [InlineData("100 001")]
    public void ANumberThatIsNotANumber_IsNotConfirmed(string brn)
        => Assert.Equal(BrnReconciliation.Unparseable,
            BrnReconciler.Reconcile(brn, Facility()).Outcome);

    /// <summary>
    /// A facility that has never requested a block has handed out nothing, so
    /// every number is unallocated -- including its own start.
    /// </summary>
    [Fact]
    public void AFacilityThatNeverRequestedABlock_ConfirmsNothing()
        => Assert.Equal(BrnReconciliation.NotYetAllocated,
            BrnReconciler.Reconcile("100000", Facility(next: 100_000)).Outcome);
}

/// <summary>
/// Covers confirmation through the registration path (draft 5.1: the centre
/// "validates and confirms the BRN as permanent").
///
/// The rule that carries the weight is that an unconfirmable BRN is never
/// refused. The birth happened and the number is already on a provisional
/// certificate in a family's hands -- withdrawing it would create exactly the
/// renumbering the block design exists to avoid.
/// </summary>
public class BrnConfirmationTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public BrnConfirmationTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);

        // A block of 200 has been granted: 100000-100199 are in devices'
        // hands, everything above is still the registry's.
        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId,
            Name = "Terekeka Village Health Post",
            DistrictId = "SS-CE-TER",
            BrnBlockStart = 100_000,
            BrnBlockNextAvailable = 100_200,
            BrnBlockEnd = 199_999
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

    private static RegisterBirthRequest Request(string brn)
        => new()
        {
            Brn = brn,
            FacilityId = FacilityId,
            ChildFullName = "Ayen Deng",
            DateOfBirth = DateTime.UtcNow.Date.AddDays(-5),
            Sex = Sex.Female,
            Plurality = BirthPlurality.Singleton,
            BirthOrder = 1,
            DeviceId = "TABLET-07"
        };

    private async Task<RegistrationResult> RegisterAsync(string brn, Guid? syncBatchId = null)
    {
        await using var db = NewDb();
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var registrar = db.Registrars.Single(r => r.RegistrarId == RegistrarId);

        var service = new BirthRegistrationService(
            db, new NoOpEventPublisher(), current,
            new DuplicateDetectionService(db, new DuplicateMatcher(),
                new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance,
                new DistrictLookup(db)),
            new DistrictLookup(db),
            Options.Create(new StatutoryRegistrationOptions()));

        return await service.RegisterAsync(
            Request(brn), registrar, Guid.CreateVersion7(), default, syncBatchId);
    }

    [Fact]
    public async Task ABrnFromAGrantedBlock_IsConfirmed()
    {
        var result = await RegisterAsync("100001");

        Assert.True(result.Succeeded);
        Assert.True(result.BrnReconciliation!.Confirmed);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal(RecordStatus.Confirmed, record.Status);
        Assert.NotNull(record.ConfirmedAtUtc);
    }

    /// <summary>
    /// The number is already printed on a provisional certificate in a
    /// family's hands. Refusing the record would force a renumbering, which
    /// is precisely what the block design exists to avoid.
    /// </summary>
    [Fact]
    public async Task AnUnallocatedBrn_IsRecordedButNotConfirmed()
    {
        var result = await RegisterAsync("150000");

        Assert.True(result.Succeeded);
        Assert.False(result.BrnReconciliation!.Confirmed);

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal("150000", record.Brn);
        Assert.Equal(RecordStatus.Provisional, record.Status);
        Assert.Null(record.ConfirmedAtUtc);
    }

    /// <summary>
    /// An unconfirmable number is the signal that a device is generating
    /// identifiers it was never granted, so it has to be findable afterwards.
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedBrn_IsAuditedWithItsReason()
    {
        await RegisterAsync("150000");

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync(log => log.Action.StartsWith("BrnUnconfirmed"));

        Assert.Equal("BrnUnconfirmed:NotYetAllocated", audit.Action);
        Assert.Equal("150000", audit.EntityId);
        Assert.Equal("TABLET-07", audit.DeviceId);
    }

    [Fact]
    public async Task ABrnFromAnotherFacilitysRange_IsNotConfirmed()
    {
        await RegisterAsync("900001");

        await using var db = NewDb();
        Assert.Equal(RecordStatus.Provisional, (await db.BirthRecords.SingleAsync()).Status);
        Assert.Equal("BrnUnconfirmed:OutsideFacilityRange",
            (await db.AuditLogs.SingleAsync(log => log.Action.StartsWith("BrnUnconfirmed"))).Action);
    }

    /// <summary>
    /// Confirmation records which batch carried the record in, so a device
    /// that turns out to be issuing bad numbers can be traced from any one of
    /// them.
    /// </summary>
    [Fact]
    public async Task ARecordArrivingBySync_RecordsTheBatchThatConfirmedIt()
    {
        var batchId = Guid.CreateVersion7();

        await using (var setup = NewDb())
        {
            setup.SyncBatches.Add(new SyncBatch
            {
                SyncBatchId = batchId,
                DeviceId = "TABLET-07",
                FacilityId = FacilityId,
                UploadedByRegistrarId = RegistrarId,
                SubmittedAtUtc = DateTime.UtcNow,
                RecordCount = 1,
                Status = SyncBatchStatus.Processing
            });
            await setup.SaveChangesAsync();
        }

        await RegisterAsync("100002", batchId);

        await using var db = NewDb();
        Assert.Equal(batchId, (await db.BirthRecords.SingleAsync()).ConfirmedBySyncBatchId);
    }

    /// <summary>
    /// A record filed straight against the central API is already at the
    /// centre; there is no batch to attribute it to.
    /// </summary>
    [Fact]
    public async Task AnOnlineRegistration_ConfirmsWithNoBatch()
    {
        await RegisterAsync("100003");

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.NotNull(record.ConfirmedAtUtc);
        Assert.Null(record.ConfirmedBySyncBatchId);
    }

    /// <summary>
    /// Status is a single enum and an amendment overwrites it. Confirmation
    /// is tracked separately for exactly that reason -- a corrected record
    /// has not stopped being reconciled.
    /// </summary>
    [Fact]
    public async Task AmendingAConfirmedRecord_DoesNotUnconfirmIt()
    {
        await RegisterAsync("100004");

        await using (var amend = NewDb())
        {
            var http = AuthTestContext.HttpContextFor();
            var current = AuthTestContext.RegistrarService(amend, http);
            var registrar = amend.Registrars.Single(r => r.RegistrarId == RegistrarId);

            var outcome = await new AmendmentService(
                    amend, new NoOpEventPublisher(), new CertificateRevocationRecorder(amend), current,
                    new DistrictLookup(amend))
                .AmendAsync("100004", new AmendBirthRecordRequest
                {
                    BirthWeightGrams = 3250,
                    Reason = "Scale re-read at the bedside.",
                    DeviceId = "TABLET-07"
                }, registrar, Guid.CreateVersion7());

            Assert.True(outcome.Succeeded);
        }

        await using var db = NewDb();
        var record = await db.BirthRecords.SingleAsync();

        Assert.Equal(RecordStatus.Amended, record.Status);
        Assert.NotNull(record.ConfirmedAtUtc);
    }
}
