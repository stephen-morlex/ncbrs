using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// A refused request is audited even though nothing it asked for happened —
/// "an attempted upload from a stolen token is the most interesting thing the
/// endpoint sees". Devices send a transaction id on every request, so their
/// refusals run inside the idempotency filter's transaction, which rolls back
/// on any non-2xx. These pin that the refusal survives that rollback.
/// </summary>
public class RefusalAuditTests : IDisposable
{
    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public RefusalAuditTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    private static AuditLog Refusal(string action = "DeviceRefused:SignatureFailed") => new()
    {
        EntityType = nameof(SyncBatch),
        EntityId = "TABLET-STOLEN",
        Action = action,
        DeviceId = "TABLET-STOLEN",
        CountyCode = "SS0101",
    };

    /// <summary>Runs the idempotency filter around an action that audits a refusal and answers 403.</summary>
    private async Task RunRefusedRequestAsync(Action<NcbrsDbContext, RefusalAudit> auditInsideAction)
    {
        await using var db = NewDb();
        var refusals = new RefusalAudit(db, NullLogger<RefusalAudit>.Instance);

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/api/sync/batches";
        // A caller-supplied id, as a device always sends: this is what makes
        // the filter wrap the action in a transaction.
        TransactionContext.Set(http, new TransactionContext(Guid.CreateVersion7(), "MobileApp", WasGenerated: false));

        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ControllerActionDescriptor()),
            [], new Dictionary<string, object?> { ["envelope"] = new ApiRequest<object> { Data = new() } }, controller: null!);

        var filter = new IdempotencyFilter(db, Options.Create(new JsonOptions()), NullLogger<IdempotencyFilter>.Instance, refusals);

        await filter.OnActionExecutionAsync(context, async () =>
        {
            auditInsideAction(db, refusals);
            await db.SaveChangesAsync();

            return new ActionExecutedContext(context, [], controller: null!)
            {
                Result = new ObjectResult(new { title = "Device not permitted to sync." }) { StatusCode = StatusCodes.Status403Forbidden },
            };
        });
    }

    /// <summary>
    /// The bug this closes. Before RefusalAudit the refusal was written with
    /// the request's work and rolled back with it -- so the attempt went
    /// unrecorded exactly when a real device (which always sends a transaction
    /// id) made it.
    /// </summary>
    [Fact]
    public async Task ARefusalSurvivesTheRollbackOfTheRequestItRefused()
    {
        await RunRefusedRequestAsync((_, refusals) => refusals.Record(Refusal()));

        await using var verify = NewDb();
        Assert.Contains(await verify.AuditLogs.ToListAsync(), row => row.Action == "DeviceRefused:SignatureFailed");
    }

    /// <summary>
    /// Precision: only refusals survive. An ordinary action's audit row from a
    /// request that then failed would assert something that never happened.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryAuditRowFromAFailedRequestIsStillRolledBack()
    {
        await RunRefusedRequestAsync((db, refusals) =>
        {
            db.AuditLogs.Add(Refusal(action: "RegisterBirth"));
            refusals.Record(Refusal());
        });

        await using var verify = NewDb();
        var actions = (await verify.AuditLogs.ToListAsync()).Select(row => row.Action).ToList();
        Assert.Contains("DeviceRefused:SignatureFailed", actions);
        Assert.DoesNotContain("RegisterBirth", actions);
    }

    /// <summary>
    /// Restoring the refusal must not resurrect the refused work -- neither
    /// what the action saved inside the rolled-back transaction nor what it
    /// added and never saved.
    /// </summary>
    [Fact]
    public async Task RestoringARefusalBringsBackNothingTheRefusedWorkTouched()
    {
        var saved = Guid.CreateVersion7();
        var unsaved = Guid.CreateVersion7();

        await RunRefusedRequestAsync((db, refusals) =>
        {
            db.Facilities.Add(new Facility { FacilityId = saved, Name = "Saved inside the transaction", CountyCode = "SS0101" });
            refusals.Record(Refusal());
        });

        // The harness saves once after the callback; a second entity added
        // afterwards and never saved is the other case.
        await RunRefusedRequestAsync((db, refusals) =>
        {
            refusals.Record(Refusal("DeviceRefused:NotEnrolled"));
            db.SaveChanges();
            db.Facilities.Add(new Facility { FacilityId = unsaved, Name = "Added, never saved", CountyCode = "SS0101" });
        });

        await using var verify = NewDb();
        Assert.False(await verify.Facilities.AnyAsync(f => f.FacilityId == saved || f.FacilityId == unsaved));
        Assert.Equal(2, await verify.AuditLogs.CountAsync(row => row.Action.StartsWith("DeviceRefused:")));
    }
}
