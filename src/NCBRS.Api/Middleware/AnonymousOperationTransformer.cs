using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NCBRS.Middleware;

/// <summary>
/// Says so in the document where an endpoint opts out of authentication.
///
/// <see cref="BearerSecuritySchemeTransformer"/> applies the bearer
/// requirement to the whole document, matching the fallback policy that puts
/// every endpoint behind a signed-in user. An endpoint marked
/// <c>[AllowAnonymous]</c> is the exception, and without this the document
/// described it as needing a token it does not need. That covers certificate
/// verification (anyone holding a certificate must be able to check it) and
/// <c>/health</c> (a monitor has no user to sign in as). A document that
/// misdescribes the service misleads everything generated from it.
///
/// An empty security list on an operation is how OpenAPI says "this one needs
/// none", overriding the document-level requirement.
/// </summary>
public class AnonymousOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security = [];
        }

        return Task.CompletedTask;
    }
}
