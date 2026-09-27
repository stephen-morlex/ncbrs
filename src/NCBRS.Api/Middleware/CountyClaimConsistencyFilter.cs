using Microsoft.AspNetCore.Mvc.Filters;
using NCBRS.Services;
using NCBRS.Web;

namespace NCBRS.Middleware;

/// <summary>
/// Refuses a district officer whose county in the identity provider
/// contradicts the county of their facility in the registry.
///
/// Reporting is scoped by the county in the token (<see cref="KeycloakCounties"/>),
/// because the reporting service cannot reach the registry. Everything else is
/// scoped by the registry. So an officer's county is recorded in two places,
/// and two places drift: someone moves an officer to another county's facility
/// and forgets their Keycloak group. Left alone, that officer would act on one
/// county's records and read another county's figures, and nothing would say
/// so. This is the one service that can see both, so it checks, on every
/// request, and refuses loudly -- the fix is an administrator's, and it gets
/// made the first morning, not discovered in an audit.
///
/// A token with **no** county group is left alone here: the API scopes from
/// the registry and does not need it. The reporting service refuses such an
/// officer on its own terms, and says why.
/// </summary>
public class CountyClaimConsistencyFilter(CurrentRegistrarService currentRegistrar, CountyLookup counties)
    : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        if (user.Identity?.IsAuthenticated == true && user.IsInRole(NcbrsRoles.DistrictOfficer))
        {
            var claimed = KeycloakCounties.Of(user);

            if (claimed.Count > 0
                && await currentRegistrar.GetAsync(context.HttpContext.RequestAborted) is { } registrar)
            {
                var registry = await counties.ForRegistrarAsync(registrar, context.HttpContext.RequestAborted);

                if (claimed.Count > 1
                    || !string.Equals(claimed[0], registry, StringComparison.OrdinalIgnoreCase))
                {
                    context.Result = ApiErrors.Result(ApiErrors.Single(
                        StatusCodes.Status403Forbidden,
                        "Your county is recorded inconsistently.",
                        "registrar",
                        $"The identity provider places you in {string.Join(", ", claimed)}; the registry places "
                        + $"your facility in {registry}. An administrator must correct one of them before you "
                        + "can continue, so you are not acting on one county while reading another's figures."));
                    return;
                }
            }
        }

        await next();
    }
}
