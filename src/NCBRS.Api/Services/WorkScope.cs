using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Whose work a caller may see, for the lists facility staff use as well as
/// oversight: facility staff their own facility, a district officer their
/// county, the Ministry the country. One rule, so the certificate list and the
/// operations view cannot drift apart.
///
/// Naming a facility outside that scope is refused, never quietly narrowed: a
/// quiet narrowing reads as "nothing waiting over there".
/// </summary>
public class WorkScope(ReviewQueueScope queueScope)
{
    /// <returns>The refusal to answer with, or the facility and county to confine the query to (null: no limit at that level).</returns>
    public async Task<(ObjectResult? Refusal, Guid? FacilityId, string? CountyCode)> ResolveAsync(
        ClaimsPrincipal user, Registrar registrar, Guid? facilityId, CancellationToken cancellationToken = default)
    {
        if (!user.IsInRole(NcbrsRoles.DistrictOfficer) && !NcbrsRoles.IsNational(user))
        {
            if (facilityId is not null && facilityId != registrar.FacilityId)
            {
                return (ApiErrors.Result(ApiErrors.Single(
                    StatusCodes.Status403Forbidden, "Another facility is not yours to see.", "facilityId",
                    "Facility staff see their own facility's work.")), null, null);
            }

            return (null, registrar.FacilityId, null);
        }

        var (refusal, county) = await queueScope.ResolveAsync(user, registrar, facilityId, cancellationToken);
        return (refusal, facilityId, county);
    }
}
