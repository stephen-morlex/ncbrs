using System.Security.Claims;

namespace NCBRS.Web;

/// <summary>
/// The county a caller belongs to, as the identity provider says: Keycloak
/// group membership under <c>/counties/</c>, mapped into the access token's
/// <c>groups</c> claim (the realm's group-membership mapper, full path on).
///
/// Why the token and not the registry: the reporting service deliberately has
/// no access to the registry (it is the system of record for nothing), so it
/// cannot look up a registrar's facility the way the API does. The county has
/// to arrive with the caller. The API, which *can* check, refuses a token whose
/// county contradicts the registry, so the two cannot drift silently
/// (<c>CountyClaimConsistencyFilter</c>).
///
/// Shared by both services for the reason <see cref="KeycloakRealmRoles"/> is:
/// two copies of "how we read this from a token" are two things that can
/// disagree, and the way they disagree is that one side stops enforcing.
/// </summary>
public static class KeycloakCounties
{
    /// <summary>The claim as Keycloak issues it.</summary>
    public const string GroupsClaim = "groups";

    /// <summary>
    /// The same claim after ASP.NET's default inbound claim mapping renames it.
    /// Read both, so turning that mapping on or off cannot make every officer
    /// countyless without anyone noticing.
    /// </summary>
    public const string MappedGroupsClaim = "http://schemas.xmlsoap.org/claims/Group";

    public const string CountyGroupPrefix = "/counties/";

    /// <summary>
    /// The county codes the token names. Normally exactly one; callers must
    /// decide what zero or several mean rather than picking one.
    /// </summary>
    public static IReadOnlyList<string> Of(ClaimsPrincipal user)
        => user.Claims
            .Where(claim => claim.Type is GroupsClaim or MappedGroupsClaim)
            .Select(claim => claim.Value)
            .Where(group => group.StartsWith(CountyGroupPrefix, StringComparison.Ordinal))
            .Select(group => group[CountyGroupPrefix.Length..])
            // A nested group below a county is not a county.
            .Where(county => county.Length > 0 && !county.Contains('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
