using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NCBRS.Middleware;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Unauthenticated requests are limited per client address; signed-in ones
/// never are. Run through a real Kestrel host wired the way the Api is —
/// forwarded headers (when configured), authentication, the limiter, then the
/// request audit — because a limiter is only as good as where it sits.
/// </summary>
public sealed class UnauthenticatedRateLimitingTests : IAsyncDisposable
{
    private const int Limit = 3;

    private WebApplication? _app;
    private int _reachedAudit;

    private async Task<HttpClient> StartAsync(params string[] trustedProxies)
    {
        var options = new UnauthenticatedRateLimitOptions { PermitsPerMinute = Limit, TrustedProxies = trustedProxies };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddUnauthenticatedRateLimiting(options);

        _app = builder.Build();

        if (trustedProxies.Length > 0)
        {
            _app.UseForwardedHeaders();
        }

        // Stands in for JWT authentication: a request carrying this header is
        // signed in. Only whether the caller is authenticated matters here.
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-Signed-In"))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "registrar")], "Bearer"));
            }

            await next();
        });

        _app.UseRateLimiter();

        // Stands in for RequestAuditMiddleware, which writes a row per request.
        _app.Use(async (context, next) =>
        {
            Interlocked.Increment(ref _reachedAudit);
            await next();
        });

        _app.MapGet("/api/certificates/revocations", () => "list");

        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string? forwardedFor = null, bool signedIn = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/certificates/revocations");
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (signedIn)
        {
            request.Headers.Add("X-Test-Signed-In", "1");
        }

        return client.SendAsync(request);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefusedPastTheLimitAndTheAuditNeverSeesIt()
    {
        using var client = await StartAsync();

        for (var i = 0; i < Limit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Get(client)).StatusCode);
        }

        var refused = await Get(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("60", refused.Headers.GetValues("Retry-After").Single());
        Assert.Contains("Too many requests", await refused.Content.ReadAsStringAsync());

        // The refused request wrote nothing: a flood cannot grow the audit table.
        Assert.Equal(Limit, _reachedAudit);
    }

    /// <summary>
    /// A registrar, a device and a district node share addresses behind NAT
    /// and must never be throttled: refusing a registration is worse than the
    /// flood this prevents.
    /// </summary>
    [Fact]
    public async Task ASignedInCallerIsNeverLimited()
    {
        using var client = await StartAsync();

        for (var i = 0; i < Limit * 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Get(client, signedIn: true)).StatusCode);
        }
    }

    /// <summary>
    /// Anyone can send X-Forwarded-For. Trusted by default, it would let a
    /// caller name a new address for every request and never be limited.
    /// </summary>
    [Fact]
    public async Task ForwardedForIsIgnoredUnlessTheProxyIsTrusted()
    {
        using var client = await StartAsync();

        for (var i = 0; i < Limit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Get(client, forwardedFor: $"203.0.113.{i}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get(client, forwardedFor: "203.0.113.99")).StatusCode);
    }

    /// <summary>
    /// Behind a trusted proxy each real client gets its own allowance, rather
    /// than the whole country sharing the proxy's.
    /// </summary>
    [Fact]
    public async Task BehindATrustedProxyEachClientHasItsOwnAllowance()
    {
        using var client = await StartAsync("127.0.0.1");

        for (var i = 0; i < Limit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Get(client, forwardedFor: "203.0.113.1")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get(client, forwardedFor: "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, forwardedFor: "203.0.113.2")).StatusCode);
    }

    /// <summary>
    /// The configured list replaces ASP.NET's defaults, which trust loopback.
    /// Otherwise anything on the API's own host could forge client addresses.
    /// </summary>
    [Fact]
    public async Task OnlyTheListedProxyIsBelieved()
    {
        using var client = await StartAsync("10.9.9.9");

        for (var i = 0; i < Limit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Get(client, forwardedFor: $"203.0.113.{i}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get(client, forwardedFor: "203.0.113.99")).StatusCode);
    }

    [Theory]
    [InlineData(0, new string[0])]
    [InlineData(120, new[] { "proxy.ncbrs.ss" })]
    public void UnusableSettingsAreRefused(int permits, string[] proxies)
        => Assert.NotNull(UnauthenticatedRateLimiting.Refusal(
            new UnauthenticatedRateLimitOptions { PermitsPerMinute = permits, TrustedProxies = proxies }));

    [Fact]
    public void TheDefaultsAreUsable()
        => Assert.Null(UnauthenticatedRateLimiting.Refusal(new UnauthenticatedRateLimitOptions()));

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
