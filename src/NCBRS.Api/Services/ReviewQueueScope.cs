using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Which part of a review queue a reviewer may see: the county rule, applied
/// to the four oversight queues -- pending corrections, amendment conflicts,
/// late registrations and duplicate candidates.
///
/// Those queues used to return the whole country unless a facility was named.
/// A district officer could not *act* on another county's items (every review
/// write checks <see cref="CurrentRegistrarService.CanActForFacilityAsync"/>),
/// but could read them: proposed changes to children's names, late-registration
/// evidence, duplicate pairs with both children's names. Reading is the part
/// that leaks. The rule is the one <see cref="CountyScopeResolver"/> applies to
/// search and the device lists: own county for a district officer, national for
/// the Ministry, and a facility outside your county is refused, not quietly
/// narrowed to an empty list that reads as "nothing waiting there".
/// </summary>
public class ReviewQueueScope(CountyScopeResolver scopes, CurrentRegistrarService currentRegistrar)
{
    /// <returns>
    /// Either the refusal to answer with, or the county to confine the queue to
    /// (null: the whole country, for the Ministry).
    /// </returns>
    public async Task<(ObjectResult? Refusal, string? CountyCode)> ResolveAsync(
        ClaimsPrincipal user,
        Registrar registrar,
        Guid? facilityId,
        CancellationToken cancellationToken = default)
    {
        var scope = await scopes.ResolveAsync(user, registrar, requestedCountyCode: null, cancellationToken);
        if (!scope.IsAllowed)
        {
            return (ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, scope.Title, scope.Field, scope.Message)), null);
        }

        if (facilityId is { } facility
            && !await currentRegistrar.CanActForFacilityAsync(registrar, facility, cancellationToken))
        {
            return (ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Another county is not yours to see.", "facilityId",
                "That facility is outside your county.")), null);
        }

        return (null, scope.Scope.CountyCode);
    }
}
