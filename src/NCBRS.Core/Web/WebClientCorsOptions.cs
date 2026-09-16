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
