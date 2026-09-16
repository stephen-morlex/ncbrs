using System.Diagnostics;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Establishes the transaction identity for every request under /api,
/// echoes it back via the X-Transaction-Id and X-Client-Id response
/// headers, and persists one RequestLog row per request (method, path,
/// status, timing) -- regardless of whether the request wrote anything to
/// the domain. This is broader than AuditLog, which only records domain
/// writes; it exists so every call the Ministry or a facility device makes
/// can be traced by a single id, even a failed or read-only one.
/// </summary>
public class RequestAuditMiddleware(RequestDelegate next, ILogger<RequestAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IServiceScopeFactory scopeFactory)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        // Seeded from headers so a request that never reaches an action
        // (an unmatched route's 404) still has an id. RequestMetaActionFilter
        // may replace this with a body-supplied one once model binding has
        // run, and MetaEnvelopeFilter then restamps these headers with the
        // resolved value before the body is written.
        var seeded = TransactionContext.FromRequest(context.Request);
        TransactionContext.Set(context, seeded);
        seeded.WriteResponseHeaders(context.Response);

        var startedAtUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await next(context);
        }
        catch (Exception)
        {
            // No global exception-handling middleware is configured yet, so
            // by the time the exception reaches us the response usually
            // hasn't been given a status code -- record it as a server
            // error ourselves before rethrowing, so RequestLog reflects
            // what actually happened rather than a stale 200.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            throw;
        }
        finally
        {
            stopwatch.Stop();

            // Read back here rather than reusing the seeded value: by now a
            // body-supplied transaction id has been promoted, and the log
            // must record the id the caller actually sees.
            var resolved = TransactionContext.Get(context);
            if (resolved is not null)
            {
                await TryPersistAsync(scopeFactory, resolved, context, startedAtUtc, stopwatch.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// Deliberately uses its own DbContext from a fresh scope rather than
    /// the request's. Sharing the request's context would flush any changes
    /// an action left tracked-but-unsaved (silently committing work the
    /// caller was told had failed), and would itself fail on the 500 path,
    /// where that context may be faulted by the very SaveChanges that threw.
    /// </summary>
    private async Task TryPersistAsync(
        IServiceScopeFactory scopeFactory,
        TransactionContext transaction,
        HttpContext context,
        DateTime startedAtUtc,
        long durationMs)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NcbrsDbContext>();

            db.RequestLogs.Add(new RequestLog
            {
                TransactionId = transaction.TransactionId,
                ClientId = transaction.ClientId,
                TransactionIdGenerated = transaction.WasGenerated,
                Method = context.Request.Method,
                Path = context.Request.Path.Value ?? string.Empty,
                StatusCode = context.Response.StatusCode,
                DurationMs = durationMs,
                StartedAtUtc = startedAtUtc
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The request itself must never fail because its own audit
            // record couldn't be written -- log and move on, matching the
            // Kafka publisher's swallow-and-log pattern.
            logger.LogError(ex, "Failed to persist RequestLog for transaction {TransactionId}", transaction.TransactionId);
        }
    }
}
