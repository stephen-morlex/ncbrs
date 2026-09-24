using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Makes a state-changing request execute at most once per transaction id,
/// however many times a device retries it.
///
/// The problem this solves is specific to the deployment: a village post's
/// link drops constantly, so a lost response is routine rather than
/// exceptional. Without this, a retry either re-registers the birth
/// (duplicate BRN -- a legal problem) or is refused outright, leaving the
/// device unable to tell whether the registration landed.
///
/// Only 2xx outcomes claim a key permanently. A rejection or a crash
/// releases it, because the safety property that matters is "never register
/// the same birth twice", not "never let a caller reuse an id".
/// </summary>
public class IdempotencyFilter(
    NcbrsDbContext db,
    IOptions<JsonOptions> jsonOptions,
    ILogger<IdempotencyFilter> logger,
    RefusalAudit refusals) : IAsyncActionFilter
{
    /// <summary>
    /// MVC's own serializer settings, not the defaults. Storing with default
    /// options would replay a PascalCase body for a response that originally
    /// went out camelCase -- a client parsing the first would break on the
    /// retry, which is precisely the case this feature exists to serve.
    /// </summary>
    private JsonSerializerOptions SerializerOptions => jsonOptions.Value.JsonSerializerOptions;

    /// <summary>
    /// How long a claim stays valid while executing. Long enough for a slow
    /// registration, short enough that a crashed request's key frees up
    /// while the device is still retrying.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var transaction = TransactionContext.Get(context.HttpContext);

        // Only guard state-changing calls that carry a caller-supplied key.
        // A GET is naturally repeatable, and a server-minted id can't collide.
        if (transaction is null || transaction.WasGenerated || !IsStateChanging(context.HttpContext.Request.Method))
        {
            await next();
            return;
        }

        var fingerprint = Fingerprint(context);

        // Decided before opening a transaction: a replay or a refusal does no
        // work and shouldn't hold a write lock while it answers.
        var existing = await db.IdempotencyRecords
            .FirstOrDefaultAsync(record => record.TransactionId == transaction.TransactionId);

        if (existing is not null && Evaluate(existing, transaction, fingerprint) is { } rejection)
        {
            context.Result = rejection;
            return;
        }

        // The claim, the domain write, and marking the key complete all live
        // in one transaction. Without this there's a window where a birth is
        // committed but its key is not: a crash in between would leave the
        // registration durable and the key still open, so the device's retry
        // would register the same birth a second time.
        await using var dbTransaction = await db.Database.BeginTransactionAsync();

        if (!await TryClaimAsync(transaction, fingerprint, existing))
        {
            await dbTransaction.RollbackAsync();

            context.Result = InProgressElsewhere(transaction);
            return;
        }

        var executed = await next();

        await FinaliseAsync(transaction, executed, dbTransaction);
    }

    private static bool IsStateChanging(string method)
        => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    /// <summary>
    /// Decides what an already-present record means for this attempt.
    /// Returns null when the caller should go ahead and reclaim it.
    /// </summary>
    private IActionResult? Evaluate(
        IdempotencyRecord existing,
        TransactionContext transaction,
        string fingerprint)
    {
        // Same key, different payload: the caller has a bug, and in a
        // registry that could mean one birth's id carrying another's data.
        if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status422UnprocessableEntity,
                "Transaction id reused with a different request.",
                "meta.transactionId",
                $"transactionId '{transaction.TransactionId}' was already used for a different request. "
                + "Use a new transactionId for a new request."));
        }

        if (existing.Status == IdempotencyStatus.Completed)
        {
            return Replay(existing);
        }

        if (existing.LeaseExpiresAtUtc > DateTime.UtcNow)
        {
            return InProgressElsewhere(transaction);
        }

        // The lease lapsed: whoever held this key died mid-request. Take it
        // over rather than leaving the registration permanently blocked.
        logger.LogWarning(
            "Reclaiming expired idempotency lease for transaction {TransactionId}", transaction.TransactionId);

        return null;
    }

    /// <summary>
    /// Takes ownership of the key inside the caller's transaction. The unique
    /// index -- not the earlier read -- is what actually serializes two
    /// devices racing on the same id.
    /// </summary>
    private async Task<bool> TryClaimAsync(
        TransactionContext transaction,
        string fingerprint,
        IdempotencyRecord? existing)
    {
        if (existing is null)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TransactionId = transaction.TransactionId,
                ClientId = transaction.ClientId,
                RequestFingerprint = fingerprint,
                Status = IdempotencyStatus.InProgress,
                LeaseExpiresAtUtc = DateTime.UtcNow.Add(LeaseDuration)
            });
        }
        else
        {
            existing.Status = IdempotencyStatus.InProgress;
            existing.LeaseExpiresAtUtc = DateTime.UtcNow.Add(LeaseDuration);
        }

        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private static ObjectResult InProgressElsewhere(TransactionContext transaction)
        => ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status409Conflict,
            "Transaction already in progress.",
            "meta.transactionId",
            $"transactionId '{transaction.TransactionId}' is currently being processed. Retry shortly."));

    /// <summary>
    /// Commits the key and the work it guarded as one unit, or discards both.
    ///
    /// A rollback is what releases the key on failure -- there's no delete to
    /// perform, because an uncommitted claim never existed. That also means a
    /// rejected request leaves no trace in this table and can be corrected
    /// and resent under the same id.
    /// </summary>
    private async Task FinaliseAsync(
        TransactionContext transaction,
        ActionExecutedContext executed,
        IDbContextTransaction dbTransaction)
    {
        var (statusCode, value) = Describe(executed);
        var succeeded = executed.Exception is null && statusCode is >= 200 and < 300;

        if (!succeeded)
        {
            await dbTransaction.RollbackAsync();

            // The work is undone; a refusal of it is not work, it is the record
            // that the attempt was made. See RefusalAudit.
            await refusals.RestoreAfterRollbackAsync();
            return;
        }

        try
        {
            // Re-read rather than holding a reference: an action may clear the
            // change tracker (RequestBrnBlock does, when retrying a
            // concurrency conflict). Still inside the transaction, so this
            // sees our own uncommitted claim.
            var record = await db.IdempotencyRecords
                .FirstAsync(r => r.TransactionId == transaction.TransactionId);

            record.Status = IdempotencyStatus.Completed;
            record.ResponseStatusCode = statusCode;
            record.ResponseBody = value is null ? null : JsonSerializer.Serialize(value, SerializerOptions);
            record.CompletedAtUtc = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await dbTransaction.CommitAsync();
        }
        catch (Exception ex)
        {
            // Failing to record the key would leave the work committed and
            // unguarded -- the exact window this transaction exists to close.
            // Undo everything instead and let the device retry.
            logger.LogError(ex, "Failed to complete idempotency record for transaction {TransactionId}; rolling back",
                transaction.TransactionId);

            await dbTransaction.RollbackAsync();
            throw;
        }
    }

    private static (int? StatusCode, object? Value) Describe(ActionExecutedContext executed)
        => executed.Result switch
        {
            ObjectResult objectResult => (objectResult.StatusCode ?? StatusCodes.Status200OK, objectResult.Value),
            StatusCodeResult statusCodeResult => (statusCodeResult.StatusCode, null),
            _ => (null, null)
        };

    /// <summary>
    /// Returns the original response. Deserialized to JsonElement so it goes
    /// back out byte-identical to what the caller missed, rather than being
    /// re-derived from state that may have moved on since.
    /// </summary>
    private static IActionResult Replay(IdempotencyRecord record)
    {
        object? value = record.ResponseBody is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(record.ResponseBody);

        return new ObjectResult(value)
        {
            StatusCode = record.ResponseStatusCode ?? StatusCodes.Status200OK
        };
    }

    private static string Fingerprint(ActionExecutingContext context)
    {
        var payload = context.ActionArguments.Values
            .Select(argument => argument is IHasRequestMeta envelope
                // Exclude meta: the transaction id is the key itself, and a
                // retry legitimately repeats it.
                ? JsonSerializer.Serialize(GetData(envelope))
                : JsonSerializer.Serialize(argument));

        var canonical = string.Join(
            '|',
            [context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value ?? string.Empty, .. payload]);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static object? GetData(IHasRequestMeta envelope)
        => envelope.GetType().GetProperty(nameof(ApiRequest<object>.Data))?.GetValue(envelope);
}
