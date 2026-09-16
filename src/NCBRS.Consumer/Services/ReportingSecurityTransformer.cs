using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NCBRS.Consumer.Services;

/// <summary>
/// Declares the bearer scheme and applies it to the operations that actually
/// require it.
///
/// The built-in generator infers nothing about security -- it does not look
/// at the authentication handlers registered, nor at what an endpoint asks
/// for. Left alone, this document describes a service that needs no
/// credentials, while every reporting endpoint sits behind the
/// `ncbrs-reporting` policy (W9). A client generated from it would send no
/// Authorization header and 401 on every dashboard call.
///
/// Unlike the API's, this is applied **per operation rather than globally**,
/// because the two services decide authentication differently. The API has a
/// fallback policy, so authentication is its default and a global
/// requirement is the truth. Here only the endpoints that say
/// `.RequireAuthorization(...)` are protected and `/health` is deliberately
/// open -- a liveness probe that needs a token is a liveness probe nothing
/// can call. Asserting it globally would document `/health` as needing
/// credentials it refuses to use.
///
/// Read from the endpoint's own authorization metadata rather than a list
/// kept here, so an endpoint added later is described correctly without
/// anyone remembering to update this.
/// </summary>
public class ReportingSecurityTransformer : IOpenApiOperationTransformer
{
    public const string SchemeName = "bearer";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var document = context.Document;

        if (document is null)
        {
            return Task.CompletedTask;
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = SchemeName,
            BearerFormat = "JWT",
            Description = "A Keycloak access token for a district officer or ministry admin."
        };

        var authorized = context.Description.ActionDescriptor.EndpointMetadata
            .Any(metadata => metadata is IAuthorizeData);

        if (authorized)
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
                }
            ];
        }

        return Task.CompletedTask;
    }
}
