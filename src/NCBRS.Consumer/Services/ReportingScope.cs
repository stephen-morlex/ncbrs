using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NCBRS.Services;
using NCBRS.Web;

namespace NCBRS.Consumer.Services;

/// <summary>
/// Which county's figures a caller may read.
///
/// The Ministry reads the whole country, or one county by naming it. A district
/// officer reads their own county and nothing else. National figures, other
/// counties' figures and the DHIS2 export are the Ministry's, as the web plan's
/// role table has always said. Before this, any district officer could read all
/// of it, including small unsuppressed counts in other counties -- one maternal
/// death in a small county is exactly the disclosure the DHIS2 export's
/// suppression rules exist to prevent.
///
/// The rule is the API's <c>CountyScopeResolver</c>, restated here because this
/// service cannot reach the registry: the officer's county comes from the token
/// (<see cref="KeycloakCounties"/>). As there, naming another county is
/// **refused, not narrowed** -- answering a question the caller did not ask
/// reads as "that county has nothing".
/// </summary>
public static class ReportingScope
{
    public readonly record struct Resolution(bool Allowed, string? CountyCode, string? Refusal)
    {
        /// <summary>A 403 in this service's error shape, or null when allowed.</summary>
        public IResult? AsRefusal()
            => Allowed ? null : Results.Json(new ApiError(Refusal!), statusCode: StatusCodes.Status403Forbidden);
    }

    /// <param name="requestedCountyCode">What the caller asked for; null means "the default for me".</param>
    public static Resolution Resolve(ClaimsPrincipal user, string? requestedCountyCode)
    {
        var requested = string.IsNullOrWhiteSpace(requestedCountyCode) ? null : requestedCountyCode;

        if (user.IsInRole(NcbrsRoles.MinistryAdmin))
        {
            // Null is the whole country.
            return new Resolution(true, requested, null);
        }

        var counties = KeycloakCounties.Of(user);

        // Fail closed. No county, or several, is an account the identity
        // provider has not placed; guessing would either show nothing or show
        // the wrong county's figures under the right one's name.
        if (counties.Count != 1)
        {
            return new Resolution(false, null,
                counties.Count == 0
                    ? "Your account has no county in the identity provider, so reporting cannot be scoped to it. "
                      + "An administrator must add you to your county's group."
                    : "Your account is in more than one county group in the identity provider. "
                      + "An administrator must leave you in exactly one.");
        }

        var own = counties[0];

        if (requested is not null && !string.Equals(requested, own, StringComparison.OrdinalIgnoreCase))
        {
            return new Resolution(false, null, "Another county's figures are not yours to see.");
        }

        return new Resolution(true, own, null);
    }
}
