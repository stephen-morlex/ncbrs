using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Writes the API's standard enveloped error from outside MVC.
///
/// Authentication and authorization failures are produced by middleware that
/// never reaches MetaEnvelopeFilter, so without this a 401 or 403 comes back
/// with an empty body -- a client that parses { meta, data } everywhere else
/// would have to special-case exactly the two responses it is most likely to
/// meet.
/// </summary>
public static class ApiErrorWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string title,
        string field,
        string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var transaction = TransactionContext.Get(context);

        var meta = new ResponseMeta(
            transaction?.TransactionId ?? Guid.CreateVersion7(),
            transaction?.ClientId,
            transaction?.WasGenerated ?? true,
            DateTime.UtcNow);

        var payload = new ApiResponse<ApiErrorResponse>(
            meta,
            ApiErrors.Single(statusCode, title, field, message));

        var serializerOptions = context.RequestServices
            .GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions;

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, serializerOptions));
    }
}
