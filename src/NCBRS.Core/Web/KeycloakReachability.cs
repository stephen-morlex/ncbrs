using Microsoft.Extensions.Logging;

namespace NCBRS.Middleware;

/// <summary>
/// Tells "this service cannot reach its identity provider" apart from "this
/// caller sent a bad token".
///
/// Both surface as a 401, and ASP.NET logs the reason at Information under
/// <c>Microsoft.AspNetCore</c>, which the shipped settings filter out. So a
/// Keycloak outage, a wrong authority or an untrusted TLS certificate made
/// every signed-in request fail while the service logged nothing — found by
/// running the Api in Production against Keycloak over HTTPS without the CA
/// trusted. From the outside that is indistinguishable from every user's token
/// being wrong, and it is the service that needs fixing, so it is logged as an
/// error. A bad token stays quiet: that is the caller's problem, and logging
/// each one would bury this line.
/// </summary>
public static class KeycloakReachability
{
    /// <summary>
    /// Microsoft.IdentityModel codes that mean the metadata was never
    /// obtained. The codes survive the library hiding values as personal
    /// data; the message wording is not relied on.
    ///
    /// <list type="bullet">
    /// <item><c>IDX20803</c> — unable to obtain configuration from the
    /// authority, when the failure reaches the handler as itself.</item>
    /// <item><c>IDX10204</c> — no issuer to validate against. This is how the
    /// failure actually surfaces on .NET 10 (verified live): the handler goes
    /// on without configuration, and the issuer here comes only from
    /// Keycloak's metadata, so no issuer means no metadata. A token from the
    /// wrong realm is <c>IDX10205</c> instead, and stays quiet.</item>
    /// <item><c>IDX10500</c> — no signing keys at all, which likewise means
    /// the key set was never fetched.</item>
    /// </list>
    /// </summary>
    public static readonly string[] ConfigurationUnavailableCodes = ["IDX20803", "IDX10204", "IDX10500"];

    private static long _lastReportedTicks;

    public static bool IsProviderUnreachable(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (ConfigurationUnavailableCodes.Any(code => current.Message.Contains(code, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Logs at most once a minute: every request fails while this lasts, and
    /// one line per request would be a flood of the same fact.
    /// </summary>
    public static void Report(ILogger logger, Exception exception, string authority)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastReportedTicks);
        if (now - last < TimeSpan.TicksPerMinute
            || Interlocked.CompareExchange(ref _lastReportedTicks, now, last) != last)
        {
            return;
        }

        logger.LogError(
            exception,
            "Cannot obtain the identity provider's metadata and signing keys from {Authority}. Every signed-in "
            + "request is refused with 401 until this is fixed. Check that Keycloak is up, that the authority is "
            + "right, and that its TLS certificate is trusted by this host.",
            authority);
    }
}
