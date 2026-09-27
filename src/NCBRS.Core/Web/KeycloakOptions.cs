namespace NCBRS.Middleware;

public class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>
    /// The realm's issuer URL. Token signing keys are discovered from here,
    /// so it must match the `iss` claim Keycloak stamps on its tokens exactly
    /// -- a trailing slash or a different hostname is enough to fail every
    /// request with a signature error.
    /// </summary>
    public string Authority { get; set; } = "http://localhost:8080/realms/ncbrs";

    /// <summary>
    /// Tokens must name this API as an intended audience. Without it, a token
    /// minted for any other client in the realm would be accepted here.
    /// </summary>
    public string Audience { get; set; } = "ncbrs-api";

    /// <summary>
    /// Whether the realm's metadata and token-signing keys must be fetched over
    /// HTTPS. **On unless Development turns it off** (the local compose stack
    /// serves Keycloak over plain HTTP).
    ///
    /// Fetched over plain HTTP, the signing keys can be replaced by anyone on
    /// the network path, who can then mint tokens this service accepts -- any
    /// role, any county. This used to default to false and was set false in the
    /// base settings, so every environment, production included, would have
    /// accepted an http:// authority without a word.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Why this configuration must not run, or null. Called at startup;
    /// outside Development, relaxing HTTPS is refused rather than warned
    /// about. A warning in a log is how a control that ships disabled stays
    /// disabled.
    ///
    /// The authority itself is checked too. With the flag on and an
    /// <c>http://</c> authority the service used to start cleanly and then fail
    /// <em>every</em> request, anonymous <c>/health</c> included, with a 500:
    /// authentication runs on every request and ASP.NET refuses the authority
    /// there. Found by running the services in Production against TLS
    /// infrastructure — the shipped default was exactly that authority.
    /// </summary>
    public string? RefusalOutsideDevelopment(bool isDevelopment)
    {
        if (isDevelopment)
        {
            return null;
        }

        if (!RequireHttpsMetadata)
        {
            return "Keycloak:RequireHttpsMetadata is false outside Development. Token-signing keys fetched over "
                   + "plain HTTP can be replaced by anyone on the path, who could then mint accepted tokens. "
                   + "Serve Keycloak over HTTPS and remove the setting.";
        }

        return Uri.TryCreate(Authority, UriKind.Absolute, out var authority) && authority.Scheme == Uri.UriSchemeHttps
            ? null
            : $"Keycloak:Authority is '{Authority}' outside Development. It must be the realm's https:// URL, "
              + "exactly as Keycloak stamps it in the tokens' iss claim; over HTTP every request would fail.";
    }
}
