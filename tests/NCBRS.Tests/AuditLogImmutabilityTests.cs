using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The audit trail is append-only, enforced at the database (WS-A2).
///
/// Design decision #5 calls the chain of custody legal evidence about a birth
/// record. Until now that was enforced by convention — nothing in the code
/// updated an audit row, so none was updated. Convention is not a control:
/// a corrected audit row is indistinguishable from a falsified one, and the
/// value of the trail is precisely that it cannot be rewritten after someone
/// disputes a registration.
///
/// These migrate rather than use EnsureCreated, deliberately. EnsureCreated
/// builds the schema from the model and never runs a migration's SQL, so the
/// triggers would simply not exist — and a test of a control that is absent
/// passes for the wrong reason.
///
/// The refusals are asserted as <see cref="DbException"/> rather than either
/// provider's own type, so the same tests prove the control on SQLite and on
/// the Postgres the central tier actually runs.
/// </summary>
public class AuditLogImmutabilityTests : IDisposable
{
    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");

    public AuditLogImmutabilityTests()
    {
        _database = TestDatabase.Create(migrated: true);
        _options = _database.Options;
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private async Task<Guid> GivenAnAuditRowAsync()
    {
        await using var db = NewDb();

        var audit = new AuditLog
        {
            EntityType = nameof(BirthRecord),
            EntityId = "100001",
            Action = "Create",
            UserId = RegistrarId,
            DeviceId = "TABLET-07"
        };

        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync();

        return audit.AuditLogId;
    }

    /// <summary>
    /// Parameterised rather than interpolated, because SQLite stores these
    /// GUIDs as uppercase text: a literal built from Guid.ToString() matches
    /// nothing, the trigger never fires, and a test of the control passes
    /// without ever reaching it.
    /// </summary>
    private async Task<int> RawAsync(string sql, params object[] parameters)
    {
        await using var db = NewDb();

        return await db.Database.ExecuteSqlRawAsync(sql, parameters);
    }

    // --- the exit condition ------------------------------------------------------

    /// <summary>
    /// The plan's exit condition: the application role provably cannot alter
    /// a written audit row. Raw SQL, bypassing every application-layer guard,
    /// because that is what an attacker holding the application's database
    /// credentials would do.
    /// </summary>
    [Fact]
    public async Task RawSqlCannotUpdateAWrittenAuditRow()
    {
        var id = await GivenAnAuditRowAsync();

        var failure = await Assert.ThrowsAnyAsync<DbException>(() =>
            RawAsync("UPDATE \"AuditLogs\" SET \"Action\" = 'Nothing happened' WHERE \"AuditLogId\" = {0}", id));

        Assert.Contains("append-only", failure.Message);

        await using var db = NewDb();
        Assert.Equal("Create", (await db.AuditLogs.SingleAsync()).Action);
    }

    [Fact]
    public async Task RawSqlCannotDeleteAWrittenAuditRow()
    {
        var id = await GivenAnAuditRowAsync();

        var failure = await Assert.ThrowsAnyAsync<DbException>(() =>
            RawAsync("DELETE FROM \"AuditLogs\" WHERE \"AuditLogId\" = {0}", id));

        Assert.Contains("append-only", failure.Message);

        await using var db = NewDb();
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    /// <summary>
    /// A blanket delete is the shape a cover-up actually takes — nobody edits
    /// one row when they can empty the table.
    /// </summary>
    [Fact]
    public async Task RawSqlCannotClearTheAuditTable()
    {
        await GivenAnAuditRowAsync();
        await GivenAnAuditRowAsync();

        await Assert.ThrowsAnyAsync<DbException>(() => RawAsync("DELETE FROM \"AuditLogs\""));

        await using var db = NewDb();
        Assert.Equal(2, await db.AuditLogs.CountAsync());
    }

    /// <summary>
    /// Append still works, which is the entire point — a control that stopped
    /// writes would stop registration.
    /// </summary>
    [Fact]
    public async Task NewAuditRowsCanStillBeAppended()
    {
        await GivenAnAuditRowAsync();
        await GivenAnAuditRowAsync();

        await using var db = NewDb();
        Assert.Equal(2, await db.AuditLogs.CountAsync());
    }

    // --- the application-layer guard -----------------------------------------------

    /// <summary>
    /// The same rule, caught earlier and with a message naming it. This guard
    /// is not the control — anything issuing SQL directly goes around it —
    /// but it fails at the line that broke the rule rather than several
    /// layers down in the provider.
    /// </summary>
    [Fact]
    public async Task ModifyingATrackedAuditRowIsRefusedByTheContext()
    {
        await GivenAnAuditRowAsync();

        await using var db = NewDb();
        var audit = await db.AuditLogs.SingleAsync();

        audit.Action = "Something else";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.Contains("append-only", failure.Message);
        Assert.Contains("Create", failure.Message);
    }

    [Fact]
    public async Task DeletingATrackedAuditRowIsRefusedByTheContext()
    {
        await GivenAnAuditRowAsync();

        await using var db = NewDb();

        db.AuditLogs.Remove(await db.AuditLogs.SingleAsync());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.Contains("append-only", failure.Message);
    }

    /// <summary>
    /// A refused audit change must not take an unrelated write down with it,
    /// and must not let one through either: nothing in the unit of work is
    /// saved, because SaveChanges never runs.
    /// </summary>
    [Fact]
    public async Task ARefusedAuditChangeSavesNothingElseInTheSameUnitOfWork()
    {
        await GivenAnAuditRowAsync();

        await using var db = NewDb();

        db.Facilities.Add(new Facility
        {
            FacilityId = Guid.CreateVersion7(),
            Name = "Kabwe Village Health Post",
            DistrictId = "D-CENTRAL-07",
            BrnBlockStart = 100_000,
            BrnBlockEnd = 199_999,
            BrnBlockNextAvailable = 100_000
        });

        (await db.AuditLogs.SingleAsync()).Action = "Tampered";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        await using var verify = NewDb();
        Assert.Empty(await verify.Facilities.ToListAsync());
    }

    /// <summary>
    /// Reading an audit row and saving other work in the same context is
    /// ordinary and must not be affected — the guard looks at what changed,
    /// not at what was loaded.
    /// </summary>
    [Fact]
    public async Task LoadingAnAuditRowWithoutChangingItDoesNotBlockOtherWrites()
    {
        await GivenAnAuditRowAsync();

        await using var db = NewDb();

        _ = await db.AuditLogs.SingleAsync();

        db.Facilities.Add(new Facility
        {
            FacilityId = Guid.CreateVersion7(),
            Name = "Lusaka Central Hospital",
            DistrictId = "D-LUSAKA-01",
            BrnBlockStart = 200_000,
            BrnBlockEnd = 299_999,
            BrnBlockNextAvailable = 200_000
        });

        await db.SaveChangesAsync();

        await using var verify = NewDb();
        Assert.Single(await verify.Facilities.ToListAsync());
    }
}
