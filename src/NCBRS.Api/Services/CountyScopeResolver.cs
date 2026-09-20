using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Which county a caller may see, resolved from their token.
///
/// One place, because two would drift — and the way they drift is that one
/// endpoint keeps enforcing the boundary while another quietly stops. The
/// rule is the same wherever it is applied: the caller's county comes from
/// their registrar's facility, resolved up the administrative hierarchy;
/// naming a different one is refused rather than narrowed; and only
/// `ministry-admin` is exempt.
///
/// **Refused rather than narrowed** is the part that must not be
/// "simplified". Silently substituting the caller's own county answers a
/// question they did not ask, and an empty result then reads as "there is
/// nothing there" rather than "you may not look there".
///
/// The county is resolved through <see cref="CountyLookup"/> (the same walk
/// up the area tree audit and reporting use), so scope, audit and the read
/// model are keyed the same way. During the transition a facility not yet
/// linked to an area falls back to its legacy district string.
/// </summary>
public class CountyScopeResolver(CountyLookup districts)
{
    public async Task<ScopeResolution> ResolveAsync(
        ClaimsPrincipal user,
        Registrar registrar,
        string? requestedDistrictId,
        CancellationToken cancellationToken = default)
    {
        if (user.IsInRole(NcbrsRoles.MinistryAdmin))
        {
            // The Ministry may narrow to a county, or see the whole register
            // by naming none. National oversight is their function.
            return ScopeResolution.Allowed(string.IsNullOrWhiteSpace(requestedDistrictId)
                ? SearchScope.National
                : SearchScope.District(requestedDistrictId));
        }

        var county = await districts.ForFacilityAsync(registrar.FacilityId, cancellationToken);

        if (string.IsNullOrWhiteSpace(county) || county == AuditLog.Unknown)
        {
            return ScopeResolution.Denied(
                "No county for this account.",
                string.Empty,
                "This account's facility has no county, so the request cannot be scoped.");
        }

        if (!string.IsNullOrWhiteSpace(requestedDistrictId)
            && !string.Equals(requestedDistrictId, county, StringComparison.OrdinalIgnoreCase))
        {
            return ScopeResolution.Denied(
                "Another county is not yours to see.",
                "districtId",
                "This is confined to your own county.");
        }

        return ScopeResolution.Allowed(SearchScope.District(county));
    }
}

/// <summary>
/// Either the scope to apply, or why the caller may not have one. Carries the
/// refusal's wording rather than a bare bool so each caller does not invent
/// its own phrasing for the same rule.
/// </summary>
public readonly record struct ScopeResolution(
    bool IsAllowed,
    SearchScope Scope,
    string Title,
    string Field,
    string Message)
{
    public static ScopeResolution Allowed(SearchScope scope) =>
        new(true, scope, string.Empty, string.Empty, string.Empty);

    public static ScopeResolution Denied(string title, string field, string message) =>
        new(false, default, title, field, message);
}
