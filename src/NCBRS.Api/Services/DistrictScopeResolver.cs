using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Which district a caller may see, resolved from their token.
///
/// One place, because two would drift — and the way they drift is that one
/// endpoint keeps enforcing the boundary while another quietly stops. The
/// rule is the same wherever it is applied: the caller's district comes from
/// their registrar record, naming a different one is refused rather than
/// narrowed, and only `ministry-admin` is exempt.
///
/// **Refused rather than narrowed** is the part that must not be
/// "simplified". Silently substituting the caller's own district answers a
/// question they did not ask, and an empty result then reads as "there is
/// nothing there" rather than "you may not look there".
/// </summary>
public class DistrictScopeResolver(NcbrsDbContext db)
{
    public async Task<ScopeResolution> ResolveAsync(
        ClaimsPrincipal user,
        Registrar registrar,
        string? requestedDistrictId,
        CancellationToken cancellationToken = default)
    {
        if (user.IsInRole(NcbrsRoles.MinistryAdmin))
        {
            // The Ministry may narrow to a district, or see the whole
            // register by naming none. National oversight is their function.
            return ScopeResolution.Allowed(string.IsNullOrWhiteSpace(requestedDistrictId)
                ? SearchScope.National
                : SearchScope.District(requestedDistrictId));
        }

        var district = await db.Facilities
            .Where(facility => facility.FacilityId == registrar.FacilityId)
            .Select(facility => facility.DistrictId)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(district))
        {
            return ScopeResolution.Denied(
                "No district for this account.",
                string.Empty,
                "This account's facility has no district, so the request cannot be scoped.");
        }

        if (!string.IsNullOrWhiteSpace(requestedDistrictId)
            && !string.Equals(requestedDistrictId, district, StringComparison.OrdinalIgnoreCase))
        {
            return ScopeResolution.Denied(
                "Another district is not yours to see.",
                "districtId",
                "This is confined to your own district.");
        }

        return ScopeResolution.Allowed(SearchScope.District(district));
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
