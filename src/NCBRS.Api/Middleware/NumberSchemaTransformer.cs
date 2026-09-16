using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NCBRS.Middleware;

/// <summary>
/// Documents a number as a number.
///
/// ASP.NET's web JSON defaults set <c>JsonNumberHandling.AllowReadingFromString</c>,
/// so the serializer will accept <c>"42"</c> as well as <c>42</c> on the way
/// in. The built-in generator reports that faithfully as
/// <c>{"type":["integer","string"]}</c> — and because one component schema
/// describes both directions, every integer the API *returns* inherits it
/// too.
///
/// The API never writes a number as a string. Left alone, 35 properties
/// across the surface — every <c>Page.total</c>, every error <c>status</c>,
/// every count — generate as <c>number | string</c> in the typed client, and
/// every arithmetic use of them needs a cast that exists only because the
/// document overstated what comes back. Swashbuckle documented all 41 as
/// plain integers; this restores that.
///
/// The document then understates what a request will tolerate, which is the
/// right direction to be wrong in: a generated client sends numbers, the API
/// accepts numbers, and nothing advertises a leniency callers should not be
/// relying on.
/// </summary>
public class NumberSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (schema.Type is not { } type || !type.HasFlag(JsonSchemaType.String))
        {
            return Task.CompletedTask;
        }

        var numeric = type & (JsonSchemaType.Integer | JsonSchemaType.Number);

        if (numeric == 0)
        {
            return Task.CompletedTask;
        }

        // Keep null where the property is genuinely nullable; drop only the
        // string alternative, which is an input convenience rather than a
        // shape the API ever produces.
        schema.Type = numeric | (type & JsonSchemaType.Null);

        // The pattern only existed to constrain the string form.
        schema.Pattern = null;

        return Task.CompletedTask;
    }
}
