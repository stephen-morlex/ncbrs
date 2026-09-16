using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NCBRS.Middleware;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The body's meta is only visible after model binding, so it's promoted
/// into the ambient transaction context by an action filter rather than by
/// the middleware. Enforcing single execution is IdempotencyFilter's job --
/// see IdempotencyFilterTests.
/// </summary>
public class RequestMetaActionFilterTests
{
    private static ActionExecutingContext ContextWith(HttpContext http, object? argument)
    {
        var actionContext = new ActionContext(http, new RouteData(), new ControllerActionDescriptor());

        var arguments = new Dictionary<string, object?>();
        if (argument is not null)
        {
            arguments["envelope"] = argument;
        }

        return new ActionExecutingContext(actionContext, [], arguments, controller: null!);
    }

    private static ApiRequest<BrnBlockRequest> Envelope(Guid? transactionId, string? clientId)
        => new()
        {
            Meta = new RequestMeta { TransactionId = transactionId, ClientId = clientId },
            Data = new BrnBlockRequest()
        };

    private static TransactionContext? Run(HttpContext http, object argument)
    {
        new RequestMetaActionFilter().OnActionExecuting(ContextWith(http, argument));
        return TransactionContext.Get(http);
    }

    [Fact]
    public void BodyMeta_OverridesTheHeaderSeededTransactionId()
    {
        var fromHeader = Guid.CreateVersion7();
        var fromBody = Guid.CreateVersion7();

        var http = new DefaultHttpContext();
        TransactionContext.Set(http, new TransactionContext(fromHeader, "HeaderClient", WasGenerated: false));

        var resolved = Run(http, Envelope(fromBody, "MobileApp"));

        Assert.NotNull(resolved);
        Assert.Equal(fromBody, resolved.TransactionId);
        Assert.Equal("MobileApp", resolved.ClientId);
        Assert.False(resolved.WasGenerated);
    }

    [Fact]
    public void AbsentBodyMeta_LeavesTheSeededContextAlone()
    {
        var seeded = Guid.CreateVersion7();

        var http = new DefaultHttpContext();
        TransactionContext.Set(http, new TransactionContext(seeded, "HeaderClient", WasGenerated: true));

        var resolved = Run(http, new ApiRequest<BrnBlockRequest> { Data = new BrnBlockRequest() });

        Assert.NotNull(resolved);
        Assert.Equal(seeded, resolved.TransactionId);
        Assert.Equal("HeaderClient", resolved.ClientId);
        Assert.True(resolved.WasGenerated);
    }

    [Fact]
    public void PartialBodyMeta_OnlyOverridesWhatItSupplies()
    {
        var seeded = Guid.CreateVersion7();

        var http = new DefaultHttpContext();
        TransactionContext.Set(http, new TransactionContext(seeded, "HeaderClient", WasGenerated: true));

        var resolved = Run(http, Envelope(transactionId: null, clientId: "MobileApp"));

        Assert.NotNull(resolved);
        Assert.Equal(seeded, resolved.TransactionId);
        Assert.Equal("MobileApp", resolved.ClientId);
    }
}
