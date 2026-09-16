using Microsoft.AspNetCore.Mvc.Filters;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Promotes the meta from a request body into the ambient
/// TransactionContext.
///
/// This has to run as an action filter rather than middleware: the body
/// isn't deserialized until model binding, which happens well after
/// RequestAuditMiddleware. The middleware seeds the context from headers so
/// that even a request which never reaches an action still has an id; this
/// filter then lets a body-supplied value take over before the action, the
/// response envelope, and the RequestLog row are written.
///
/// Enforcing that the id is used once is IdempotencyFilter's job, which
/// runs immediately after this one.
/// </summary>
public class RequestMetaActionFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var current = TransactionContext.Get(context.HttpContext);
        if (current is null)
        {
            return;
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is IHasRequestMeta { Meta: { } meta })
            {
                TransactionContext.Set(context.HttpContext, current.MergeFrom(meta));
                return;
            }
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
