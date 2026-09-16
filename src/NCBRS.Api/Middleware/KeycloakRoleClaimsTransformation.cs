using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using NCBRS.Web;

namespace NCBRS.Middleware;

/// <summary>
/// Flattens Keycloak's realm roles into ordinary role claims for every
/// authenticated request.
///
/// The reading itself lives in <see cref="KeycloakRealmRoles"/>, shared with
/// `NCBRS.Consumer` — two copies of "how we read roles from a token" is two
/// things that can disagree, and the way they disagree is that one service
/// silently stops enforcing.
///
/// Run as a transformation rather than left to per-endpoint handling because
/// silent authorization failure is the dangerous kind: a role policy that
/// matches nothing looks exactly like one that is working.
/// </summary>
public class KeycloakRoleClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        KeycloakRealmRoles.Apply(principal);

        return Task.FromResult(principal);
    }
}
