using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace NCBRS.Middleware;

/// <summary>
/// Flattens Keycloak's realm roles into ordinary role claims.
///
/// Keycloak nests them inside a JSON object claim -- {"realm_access":
/// {"roles":["facility-registrar"]}} -- which ASP.NET's role machinery does
/// not understand, so [Authorize(Roles = ...)] and role policies would
/// silently match nothing without this. Silent authorization failure being
/// the dangerous kind, this runs for every authenticated request rather than
/// being left to per-endpoint handling.
/// </summary>
public class KeycloakRoleClaimsTransformation : IClaimsTransformation
{
    public const string RealmAccessClaim = "realm_access";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;

        if (identity is null || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        var realmAccess = principal.FindFirst(RealmAccessClaim)?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return Task.FromResult(principal);
        }

        foreach (var role in ReadRoles(realmAccess))
        {
            // TransformAsync can run more than once per request in some
            // pipelines; adding a duplicate role claim is harmless but noisy.
            if (!principal.IsInRole(role))
            {
                identity.AddClaim(new Claim(identity.RoleClaimType, role));
            }
        }

        return Task.FromResult(principal);
    }

    private static IEnumerable<string> ReadRoles(string realmAccessJson)
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
