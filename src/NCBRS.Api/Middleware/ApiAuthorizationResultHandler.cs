using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace NCBRS.Middleware;

/// <summary>
/// Replaces the framework's empty 403 with the API's standard enveloped
/// error, so a caller refused by a role policy gets the same shape -- and
/// the same transaction id -- as every other rejection.
/// </summary>
public class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "Forbidden.",
                "authorization",
                "Your account does not hold a role permitted to perform this action.");

            return;
        }

        // Challenges (401) are left to the authentication handler, which
        // still needs to emit its WWW-Authenticate header.
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
