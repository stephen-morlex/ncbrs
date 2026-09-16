using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the four situations the idempotency design exists for: a genuine
/// duplicate submit, a response lost in transit, a process that crashed
/// mid-request, and a rejected payload the caller wants to correct.
/// </summary>
public class IdempotencyFilterTests : IDisposable
{
    private readonly TestDatabase _database;
    private readonly DbContextOptions<NcbrsDbContext> _options;

    public IdempotencyFilterTests()
    {
        _database = TestDatabase.Create();
        _options = _database.Options;

        using var db = new NcbrsDbContext(_options);
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_options);

    /// <summary>
    /// Built with MVC's own JSON settings, so the stored-and-replayed body
    /// matches what the original response actually put on the wire.
    /// </summary>
    private static IdempotencyFilter CreateFilter(NcbrsDbContext db)
        => new(db, Options.Create(new JsonOptions()), NullLogger<IdempotencyFilter>.Instance);

    private static ApiRequest<BrnBlockRequest> Envelope(int blockSize = 200)
        => new() { Data = new BrnBlockRequest { BlockSize = blockSize } };

    private static ActionExecutingContext ContextFor(HttpContext http, object argument)
    {
        var actionContext = new ActionContext(http, new RouteData(), new ControllerActionDescriptor());
        return new ActionExecutingContext(
            actionContext, [], new Dictionary<string, object?> { ["envelope"] = argument }, controller: null!);
    }

    private static HttpContext HttpFor(Guid transactionId)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/api/BirthRecords/register";
        TransactionContext.Set(http, new TransactionContext(transactionId, "MobileApp", WasGenerated: false));
        return http;
    }

    /// <summary>Runs the filter with an action that returns the given result.</summary>
    private async Task<(bool ActionRan, ActionExecutingContext Context)> RunAsync(
        Guid transactionId,
        IActionResult actionResult,
        object? argument = null,
        Exception? thrownByAction = null)
    {
        await using var db = NewDb();
        var http = HttpFor(transactionId);
        var context = ContextFor(http, argument ?? Envelope());
        var actionRan = false;

        var filter = CreateFilter(db);

        await filter.OnActionExecutionAsync(context, () =>
        {
            actionRan = true;
            var executed = new ActionExecutedContext(context, [], controller: null!)
            {
                Result = actionResult
            };

            if (thrownByAction is not null)
            {
                executed.Exception = thrownByAction;
            }

            return Task.FromResult(executed);
        });

        return (actionRan, context);
    }

    private static ObjectResult Ok(object value) => new(value) { StatusCode = StatusCodes.Status200OK };

    [Fact]
    public async Task FirstUseOfAKey_ExecutesAndRecordsTheOutcome()
    {
        var key = Guid.CreateVersion7();

        var (actionRan, _) = await RunAsync(key, Ok(new { blockStart = 100, blockEnd = 199 }));

        Assert.True(actionRan);

        await using var db = NewDb();
        var record = await db.IdempotencyRecords.SingleAsync();
        Assert.Equal(IdempotencyStatus.Completed, record.Status);
        Assert.Equal(StatusCodes.Status200OK, record.ResponseStatusCode);
        Assert.NotNull(record.CompletedAtUtc);
    }

    /// <summary>
    /// The lost-response case: the work already happened, so the retry must
    /// return the original answer instead of registering a second birth.
    /// </summary>
    [Fact]
    public async Task RetryAfterSuccess_ReplaysTheOriginalResponse_WithoutReExecuting()
    {
        var key = Guid.CreateVersion7();

        await RunAsync(key, Ok(new { blockStart = 100, blockEnd = 199 }));

        // The device never saw the response and sends the same request again.
        var (actionRan, context) = await RunAsync(key, Ok(new { blockStart = 999, blockEnd = 1999 }));

        Assert.False(actionRan); // critically, the action did NOT run again

        var replayed = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status200OK, replayed.StatusCode);
        Assert.Contains("\"blockStart\":100", System.Text.Json.JsonSerializer.Serialize(replayed.Value));
    }

    /// <summary>
    /// The crash case the whole redesign was for: a holder died mid-request,
    /// so once its lease lapses the retry takes over rather than being
    /// locked out of registering the birth forever.
    /// </summary>
    [Fact]
    public async Task RetryAfterACrash_TakesOverTheExpiredLease()
    {
        var key = Guid.CreateVersion7();

        // A claim left behind by a process that died before finishing.
        await using (var seed = NewDb())
        {
            seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TransactionId = key,
                RequestFingerprint = await FingerprintOfDefaultEnvelopeAsync(),
                Status = IdempotencyStatus.InProgress,
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5)
            });
            await seed.SaveChangesAsync();
        }

        var (actionRan, context) = await RunAsync(key, Ok(new { blockStart = 100, blockEnd = 199 }));

        Assert.True(actionRan);
        Assert.Null(context.Result);

        await using var db = NewDb();
        var record = await db.IdempotencyRecords.SingleAsync();
        Assert.Equal(IdempotencyStatus.Completed, record.Status);
    }

    [Fact]
    public async Task ConcurrentDuplicate_IsRefusedWhileTheLeaseIsLive()
    {
        var key = Guid.CreateVersion7();

        await using (var seed = NewDb())
        {
            seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TransactionId = key,
                RequestFingerprint = await FingerprintOfDefaultEnvelopeAsync(),
                Status = IdempotencyStatus.InProgress,
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(2)
            });
            await seed.SaveChangesAsync();
        }

        var (actionRan, context) = await RunAsync(key, Ok(new { }));

        Assert.False(actionRan);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
    }

    /// <summary>
    /// A rejected payload must not burn the key -- the caller has to be able
    /// to fix it and resend without being locked out.
    /// </summary>
    [Fact]
    public async Task RejectedRequest_ReleasesTheKeyForRetry()
    {
        var key = Guid.CreateVersion7();

        var badRequest = new ObjectResult(new { error = "invalid" }) { StatusCode = StatusCodes.Status400BadRequest };
        await RunAsync(key, badRequest);

        await using (var db = NewDb())
        {
            Assert.Empty(await db.IdempotencyRecords.ToListAsync());
        }

        // Same key again, now with a valid payload: must be allowed through.
        var (actionRan, _) = await RunAsync(key, Ok(new { blockStart = 100 }));
        Assert.True(actionRan);
    }

    /// <summary>
    /// The window this design closes: an action that commits a birth and then
    /// dies before its key is recorded. If the two weren't in one
    /// transaction, the registration would be durable while the key stayed
    /// open -- so the device's retry would register the same birth twice.
    /// Both must be undone together.
    /// </summary>
    [Fact]
    public async Task CrashAfterTheDomainWrite_RollsBackBothTheWriteAndTheKey()
    {
        var key = Guid.CreateVersion7();
        var brn = "IDEMPOTENCY-ATOMICITY-1";

        await using (var db = NewDb())
        {
            var http = HttpFor(key);
            var context = ContextFor(http, Envelope());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateFilter(db).OnActionExecutionAsync(context, async () =>
                {
                    // Stand-in for the registration the action commits...
                    db.People.Add(new Person { FullName = "Rollback Test", NationalIdRef = brn });
                    await db.SaveChangesAsync();

                    // ...and then the process dies before the key is marked.
                    throw new InvalidOperationException("crash after commit");
                }));
        }

        await using var verify = NewDb();
        Assert.Empty(await verify.IdempotencyRecords.ToListAsync());
        Assert.False(await verify.People.AnyAsync(p => p.NationalIdRef == brn));
    }

    [Fact]
    public async Task ActionThatThrows_ReleasesTheKeyForRetry()
    {
        var key = Guid.CreateVersion7();

        await RunAsync(key, new EmptyResult(), thrownByAction: new InvalidOperationException("boom"));

        await using var db = NewDb();
        Assert.Empty(await db.IdempotencyRecords.ToListAsync());
    }

    /// <summary>
    /// Reusing a key for different content is a client bug. In a registry it
    /// could mean one birth's transaction id carrying another's data, so it
    /// is refused rather than silently replayed.
    /// </summary>
    [Fact]
    public async Task KeyReusedWithADifferentPayload_IsRejectedAs422()
    {
        var key = Guid.CreateVersion7();

        await RunAsync(key, Ok(new { blockStart = 100 }), Envelope(blockSize: 200));

        var (actionRan, context) = await RunAsync(key, Ok(new { blockStart = 100 }), Envelope(blockSize: 500));

        Assert.False(actionRan);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
    }

    [Fact]
    public async Task ServerGeneratedId_IsNotGuarded()
    {
        await using var db = NewDb();
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/api/BirthRecords/register";
        TransactionContext.Set(http, new TransactionContext(Guid.CreateVersion7(), null, WasGenerated: true));

        var context = ContextFor(http, Envelope());
        var actionRan = false;

        await CreateFilter(db)
            .OnActionExecutionAsync(context, () =>
            {
                actionRan = true;
                return Task.FromResult(new ActionExecutedContext(context, [], controller: null!)
                {
                    Result = Ok(new { })
                });
            });

        Assert.True(actionRan);
        Assert.Empty(await db.IdempotencyRecords.ToListAsync());
    }

    /// <summary>
    /// Reproduces the fingerprint the filter computes for the default
    /// envelope, so seeded records match a real request.
    /// </summary>
    private async Task<string> FingerprintOfDefaultEnvelopeAsync()
    {
        var key = Guid.CreateVersion7();
        await RunAsync(key, Ok(new { }));

        await using var db = NewDb();
        var record = await db.IdempotencyRecords.SingleAsync(r => r.TransactionId == key);
        var fingerprint = record.RequestFingerprint;

        db.IdempotencyRecords.Remove(record);
        await db.SaveChangesAsync();

        return fingerprint;
    }
}
