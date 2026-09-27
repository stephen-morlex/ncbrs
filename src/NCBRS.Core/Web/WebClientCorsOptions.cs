using Microsoft.Extensions.Configuration;

namespace NCBRS.Web;

/// <summary>
/// Which browser origins may call a service (plan W5).
///
/// The management site talks to two services — the registration API and the
/// consumer's reporting endpoints — so both must permit it, and both must
/// permit the *same* origins. The list and the policy name live here so that
/// agreement is structural; each host still writes its own `AddCors` call,
/// because the policy is a security control and belongs where a reviewer
/// looks for it rather than behind a helper in a shared library.
///
/// This type deliberately has no ASP.NET Core dependency: Core is the domain
/// library, and a worker like NCBRS.Relay has no business gaining a web stack
/// because the API needed a CORS policy.
/// </summary>
public class WebClientCorsOptions
{
    public const string SectionName = "WebClientCors";

    /// <summary>The policy name both services register it under.</summary>
    public const string PolicyName = "ncbrs-web";

    /// <summary>
    /// Exact origins — scheme, host and port. No wildcard, and no pattern
    /// matching.
    ///
    /// Empty means no browser may call this service. That is the correct
    /// state for a deployment with no web front end, not something to paper
    /// over with a permissive default: `AllowAnyOrigin` would let any page on
    /// the internet call a registry that can withdraw a legal identity.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Why these origins must not be used, or null. Called by both services at
    /// startup.
    ///
    /// - **A wildcard is refused everywhere.** The "no wildcard" above was a
    ///   comment, not a check. Configured as the only origin, ASP.NET answers
    ///   <c>Access-Control-Allow-Origin: *</c> to every site on the internet
    ///   (verified against this API); mixed with others it is inert today, but
    ///   one edit away from the first case.
    /// - **Each entry must be a bare origin.** A trailing slash or a path never
    ///   equals a browser's <c>Origin</c> header, so the site would fail every
    ///   call as a CORS error with nothing pointing at the setting.
    /// - **Outside Development, HTTPS only.** An <c>http://</c> origin is the
    ///   management site served in cleartext, handing its tokens — which can
    ///   withdraw a legal identity — to anyone on the path.
    /// </summary>
    public string? Refusal(bool isDevelopment)
    {
        foreach (var origin in AllowedOrigins)
        {
            if (origin.Contains('*'))
            {
                return $"{SectionName}:AllowedOrigins contains '{origin}'. Wildcards are refused: as the only "
                       + "origin, ASP.NET allows every site on the internet. List the site's exact origin.";
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
            {
                return $"{SectionName}:AllowedOrigins contains '{origin}', which is not a bare origin "
                       + "(scheme://host[:port], no path or trailing slash) and would never match a browser's Origin header.";
            }

            if (!isDevelopment && uri.Scheme != Uri.UriSchemeHttps)
            {
                return $"{SectionName}:AllowedOrigins contains '{origin}' outside Development. The management site "
                       + "must be served over HTTPS: over HTTP its tokens are readable by anyone on the path.";
            }
        }

        return null;
    }

    /// <summary>
    /// Bound without the configuration binder, which Core does not reference.
    /// </summary>
    public static WebClientCorsOptions From(IConfiguration configuration)
        => new()
        {
            AllowedOrigins =
            [
                .. configuration.GetSection($"{SectionName}:AllowedOrigins")
                    .GetChildren()
                    .Select(entry => entry.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
            ]
        };
}
