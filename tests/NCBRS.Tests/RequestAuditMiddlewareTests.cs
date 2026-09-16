using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NCBRS.Data;
using NCBRS.Middleware;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers RequestAuditMiddleware directly against a DefaultHttpContext and
/// a controllable RequestDelegate, rather than spinning up a full test
/// host -- avoids depending on a live Kafka broker for
/// BirthRecordDashboardConsumer, which the app wires up as a hosted
/// service.
/// </summary>
public class RequestAuditMiddlewareTests : IDisposable
{
    private readonly TestDatabase _database;
    private readonly ServiceProvider _provider;

    public RequestAuditMiddlewareTests()
    {
        _database = TestDatabase.Create();

        // Registered from the harness's options rather than through
        // AddDbContext, so every context this test resolves shares the one
        // connection the harness owns -- which is what makes the Postgres
        // path's rollback cover the middleware's writes too.
        var services = new ServiceCollection();
        services.AddScoped(_ => new NcbrsDbContext(_database.Options));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    private RequestAuditMiddleware CreateMiddleware(RequestDelegate next)
        => new(next, NullLogger<RequestAuditMiddleware>.Instance);

    private NcbrsDbContext QueryDb() => new(_database.Options);

    private static DefaultHttpContext RequestFor(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    [Fact]
    public async Task ApiRequest_GetsTransactionIdHeaderAndRequestLogRow()
    {
        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        });

        var context = RequestFor("POST", "/api/BirthRecords/register");

        await middleware.InvokeAsync(context, ScopeFactory);

        Assert.True(context.Response.Headers.ContainsKey("X-Transaction-Id"));
        var transactionId = Guid.Parse(context.Response.Headers["X-Transaction-Id"].ToString());

        await using var db = QueryDb();
        var log = await db.RequestLogs.SingleAsync();
        Assert.Equal(transactionId, log.TransactionId);
        Assert.Equal("POST", log.Method);
        Assert.Equal("/api/BirthRecords/register", log.Path);
        Assert.Equal(StatusCodes.Status201Created, log.StatusCode);
    }

    [Fact]
    public async Task NonApiRequest_IsPassedThroughWithoutTransactionIdOrRequestLog()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = RequestFor("GET", "/swagger/index.html");

        await middleware.InvokeAsync(context, ScopeFactory);

        Assert.True(nextCalled);
        Assert.False(context.Response.Headers.ContainsKey("X-Transaction-Id"));

        await using var db = QueryDb();
        Assert.Empty(await db.RequestLogs.ToListAsync());
    }

    [Fact]
    public async Task FailedRequest_StillPersistsRequestLogWith500_AndRethrows()
    {
        var middleware = CreateMiddleware(ctx => throw new InvalidOperationException("boom"));

        var context = RequestFor("POST", "/api/BirthRecords/register");

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, ScopeFactory));

        await using var db = QueryDb();
        var log = await db.RequestLogs.SingleAsync();
        Assert.Equal(StatusCodes.Status500InternalServerError, log.StatusCode);
    }

    [Fact]
    public async Task ClientSuppliedTransactionId_IsHonouredAndEchoed()
    {
        var supplied = Guid.CreateVersion7();
        var middleware = CreateMiddleware(ctx => Task.CompletedTask);

        var context = RequestFor("POST", "/api/BirthRecords/register");
        context.Request.Headers["X-Transaction-Id"] = supplied.ToString();
        context.Request.Headers["X-Client-Id"] = "MobileApp";

        await middleware.InvokeAsync(context, ScopeFactory);

        Assert.Equal(supplied.ToString(), context.Response.Headers["X-Transaction-Id"].ToString());
        Assert.Equal("MobileApp", context.Response.Headers["X-Client-Id"].ToString());

        await using var db = QueryDb();
        var log = await db.RequestLogs.SingleAsync();
        Assert.Equal(supplied, log.TransactionId);
        Assert.Equal("MobileApp", log.ClientId);
        Assert.False(log.TransactionIdGenerated);
    }

    [Fact]
    public async Task MalformedTransactionId_FallsBackToAGeneratedOne_RatherThanRejecting()
    {
        var middleware = CreateMiddleware(ctx => Task.CompletedTask);

        var context = RequestFor("POST", "/api/BirthRecords/register");
        context.Request.Headers["X-Transaction-Id"] = "not-a-uuid";

        await middleware.InvokeAsync(context, ScopeFactory);

        // A bad tracking header must not cost us a birth registration.
        Assert.True(Guid.TryParse(context.Response.Headers["X-Transaction-Id"].ToString(), out _));

        await using var db = QueryDb();
        var log = await db.RequestLogs.SingleAsync();
        Assert.True(log.TransactionIdGenerated);
    }

    /// <summary>
    /// RequestLog is the audit of what arrived, so a device that retries a
    /// dropped request appears once per attempt. Suppressing the second row
    /// would hide the retry from the audit trail. Preventing the *work* from
    /// happening twice is IdempotencyRecord's job, not this table's.
    /// </summary>
    [Fact]
    public async Task RetriedTransactionId_IsAuditedOncePerAttempt()
    {
        var retried = Guid.CreateVersion7();
        var middleware = CreateMiddleware(ctx => Task.CompletedTask);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            var context = RequestFor("POST", "/api/BirthRecords/register");
            context.Request.Headers["X-Transaction-Id"] = retried.ToString();
            await middleware.InvokeAsync(context, ScopeFactory);
        }

        await using var db = QueryDb();
        var logs = await db.RequestLogs.Where(r => r.TransactionId == retried).ToListAsync();
        Assert.Equal(3, logs.Count);
    }

    [Fact]
    public async Task EachRequest_GetsADistinctTransactionId()
    {
        var middleware = CreateMiddleware(ctx => Task.CompletedTask);

        var first = RequestFor("GET", "/api/BirthRecords/abc");
        await middleware.InvokeAsync(first, ScopeFactory);

        var second = RequestFor("GET", "/api/BirthRecords/def");
        await middleware.InvokeAsync(second, ScopeFactory);

        Assert.NotEqual(
            first.Response.Headers["X-Transaction-Id"].ToString(),
            second.Response.Headers["X-Transaction-Id"].ToString());

        await using var db = QueryDb();
        Assert.Equal(2, await db.RequestLogs.CountAsync());
    }
}
