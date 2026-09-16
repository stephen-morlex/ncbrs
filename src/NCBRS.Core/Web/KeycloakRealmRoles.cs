using System.Security.Claims;
using System.Text.Json;

namespace NCBRS.Web;

/// <summary>
/// Flattens Keycloak's realm roles into ordinary role claims (W9).
///
/// Keycloak nests them inside a JSON object claim —
/// <c>{"realm_access": {"roles": ["district-officer"]}}</c> — which ASP.NET's
/// role machinery does not understand. Without flattening,
/// <c>RequireRole(...)</c> matches nothing and every authorised caller is
/// refused; worse, a policy that matches nothing looks exactly like a policy
/// that is working.
///
/// Shared by both services rather than written twice. Two copies of "how we
/// read roles from a token" is two things that can disagree, and the way they
/// disagree is that one service silently stops enforcing.
///
/// Deliberately free of ASP.NET types, so `NCBRS.Core` stays the domain
/// library and `NCBRS.Relay` — a worker — does not inherit a web stack. Each
/// host wires it in its own way: the API through `IClaimsTransformation`, the
/// consumer through the bearer handler's token-validated event.
/// </summary>
public static class KeycloakRealmRoles
{
    public const string RealmAccessClaim = "realm_access";

    /// <summary>
    /// Adds a role claim for each realm role on the principal. Safe to call
    /// more than once — some pipelines transform a principal repeatedly, and
    /// duplicate role claims are harmless but noisy.
    /// </summary>
    public static void Apply(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return;
        }

        var realmAccess = principal.FindFirst(RealmAccessClaim)?.Value;

        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return;
        }

        foreach (var role in Read(realmAccess))
        {
            if (!principal.IsInRole(role))
            {
                identity.AddClaim(new Claim(identity.RoleClaimType, role));
            }
        }
    }

    public static IEnumerable<string> Read(string realmAccessJson)
    {
        try
        {
            using var document = JsonDocument.Parse(realmAccessJson);

            if (!document.RootElement.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return roles.EnumerateArray()
                .Where(role => role.ValueKind == JsonValueKind.String)
                .Select(role => role.GetString()!)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .ToList();
        }
        catch (JsonException)
        {
            // A malformed claim means no roles, never a crash: the request
            // then fails authorization, which is the safe direction.
            return [];
        }
    }
}
