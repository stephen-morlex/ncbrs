using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace NCBRS.Middleware;

/// <summary>
/// Limits requests that carry no valid token, per client address.
///
/// Anyone can call certificate verification, the revocation list, the
/// offline bundle and <c>/health</c>, and anyone can send a request with a
/// bogus token. Before this each one wrote a <c>RequestLog</c> row, and the
/// list endpoints read and signed the whole national revocation list, so an
/// unauthenticated caller could grow the system of record's database without
/// bound.
///
/// **Only unauthenticated requests are limited.** The limiter runs after token
/// validation, so a device syncing, a district node forwarding and a registrar
/// working are never throttled however many share an address behind NAT —
/// throttling a registration is a worse outcome than the one this prevents.
/// It runs before the request audit, so a rejected request writes nothing.
/// </summary>
public class UnauthenticatedRateLimitOptions
{
    public const string SectionName = "UnauthenticatedRateLimit";

    /// <summary>
    /// Per client address, per minute. Generous for its callers — a clerk
    /// scanning certificates, a tablet refreshing its bundle, a monitor
    /// polling health — and small beside what a flood needs.
    /// </summary>
    public int PermitsPerMinute { get; set; } = 120;

    /// <summary>
    /// Reverse proxies whose <c>X-Forwarded-For</c> is believed. **Empty by
    /// default, and then the header is ignored**: anyone can send it, so
    /// trusting it unconditionally would let a caller pick a new address for
    /// every request and walk straight past the limit.
    ///
    /// Behind a proxy that is not listed, every caller appears as the proxy
    /// and shares one allowance. Rejections are logged with the address they
    /// were counted against, which is how that shows up.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];
}

public static class UnauthenticatedRateLimiting
{
    public const string ClientPartitionPrefix = "client:";

    private static readonly ConcurrentDictionary<string, DateTime> LastLogged = new();

    /// <summary>
    /// Which bucket a request counts against, or null for no limit. Separated
    /// from the registration so the rule itself can be tested.
    /// </summary>
    public static string? PartitionFor(HttpContext context)
        => context.User.Identity?.IsAuthenticated == true
            ? null
            : ClientPartitionPrefix + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    public static IServiceCollection AddUnauthenticatedRateLimiting(
        this IServiceCollection services, UnauthenticatedRateLimitOptions options)
    {
        if (options.TrustedProxies.Length > 0)
        {
            services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                // Replace the defaults (loopback) with exactly what was configured.
                forwarded.KnownIPNetworks.Clear();
                forwarded.KnownProxies.Clear();
                foreach (var proxy in options.TrustedProxies)
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                }
            });
        }

        return services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                PartitionFor(context) is { } partition
                    ? RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = options.PermitsPerMinute,
                        TokensPerPeriod = options.PermitsPerMinute,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    })
                    : RateLimitPartition.GetNoLimiter("authenticated"));

            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (rejected, cancellationToken) =>
            {
                var context = rejected.HttpContext;
                context.Response.Headers.RetryAfter = "60";

                // Once a minute per address, not once per rejection: a flood
                // must not become a flood of log lines instead.
                var partition = PartitionFor(context) ?? "authenticated";
                var now = DateTime.UtcNow;

                // Bounded: a flood from many addresses must not grow this
                // without limit either. Clearing only costs a repeated line.
                if (LastLogged.Count > 10_000)
                {
                    LastLogged.Clear();
                }

                if (LastLogged.TryGetValue(partition, out var last) is false || now - last > TimeSpan.FromMinutes(1))
                {
                    LastLogged[partition] = now;
                    context.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(UnauthenticatedRateLimiting))
                        .LogWarning(
                            "Unauthenticated requests from {Partition} exceeded {Limit}/min and are being refused. "
                            + "If this is a reverse proxy's address, list it in {Setting}.",
                            partition, options.PermitsPerMinute, $"{UnauthenticatedRateLimitOptions.SectionName}:TrustedProxies");
                }

                await ApiErrorWriter.WriteAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Too many requests.",
                    "request",
                    $"More than {options.PermitsPerMinute} requests a minute without signing in. Try again shortly.");
            };
        });
    }

    /// <summary>
    /// Why these settings must not be used, or null. A trusted proxy that is
    /// not an IP address would otherwise fail on the first request rather
    /// than at startup, and a limit of zero refuses every public caller.
    /// </summary>
    public static string? Refusal(UnauthenticatedRateLimitOptions options)
    {
        if (options.PermitsPerMinute < 1)
        {
            return $"{UnauthenticatedRateLimitOptions.SectionName}:PermitsPerMinute must be at least 1.";
        }

        var invalid = options.TrustedProxies.Where(proxy => !IPAddress.TryParse(proxy, out _)).ToList();
        return invalid.Count == 0
            ? null
            : $"{UnauthenticatedRateLimitOptions.SectionName}:TrustedProxies must be IP addresses: {string.Join(", ", invalid)}.";
    }
}
