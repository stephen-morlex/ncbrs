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
    /// False only for the local compose stack, which serves Keycloak over
    /// plain HTTP.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; }
}
