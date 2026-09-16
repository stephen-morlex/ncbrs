using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NCBRS.Middleware;

/// <summary>
/// Declares the bearer scheme and applies it to the whole document.
///
/// Unlike Swashbuckle, the built-in generator adds nothing about security on
/// its own -- it does not infer a scheme from the authentication handlers
/// that are registered. Without this the document would describe an API that
/// needs no credentials, while every endpoint sits behind
/// <c>FallbackPolicy = RequireAuthenticatedUser()</c>. A generated client
/// would then send no Authorization header and every call would 401.
///
/// Applied globally rather than per operation for the same reason the
/// fallback policy is global: authentication is the default here and opting
/// out is the exception, so the document should say so once rather than
/// repeat it 36 times and be wrong the first time someone forgets.
/// </summary>
public class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeName = "bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = SchemeName,
            BearerFormat = "JWT",
            Description = "Paste a Keycloak access token (without the \"Bearer \" prefix)."
        };

        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
            }
        ];

        return Task.CompletedTask;
    }
}
