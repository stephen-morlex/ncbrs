using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Wraps every controller response in the { meta, data } envelope, so a
/// caller parses one shape whether the call succeeded, was rejected, or
/// failed -- and always gets back the transaction id it sent.
///
/// Applied as a global filter rather than by each action, so a new endpoint
/// cannot forget it. Note this covers responses produced by MVC; a request
/// that never reaches a controller (an unmatched route's 404) still gets
/// the X-Transaction-Id header but no envelope.
/// </summary>
public class MetaEnvelopeFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var transaction = TransactionContext.Get(context.HttpContext);

        if (transaction is not null)
        {
            // Restamp: the middleware seeded these from headers, but a
            // body-supplied meta may have replaced the context since.
            transaction.WriteResponseHeaders(context.HttpContext.Response);

            var meta = new ResponseMeta(
                transaction.TransactionId,
                transaction.ClientId,
                transaction.WasGenerated,
                DateTime.UtcNow);

            switch (context.Result)
            {
                case ObjectResult objectResult:
                    objectResult.Value = new ApiResponse<object?>(meta, objectResult.Value);
                    // Cleared so the formatter serializes the envelope's own
                    // runtime type instead of the action's declared one.
                    objectResult.DeclaredType = null;
                    break;

                // Results with no body of their own (NotFound(), NoContent())
                // still get an envelope, so clients never have to special-case
                // an empty response to find the transaction id.
                case StatusCodeResult statusCodeResult:
                    context.Result = new ObjectResult(new ApiResponse<object?>(meta, null))
                    {
                        StatusCode = statusCodeResult.StatusCode
                    };
                    break;
            }
        }

        await next();
    }
}
