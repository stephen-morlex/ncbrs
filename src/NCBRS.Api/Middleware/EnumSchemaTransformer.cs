using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NCBRS.Middleware;

/// <summary>
/// Says out loud that a string enum is a string.
///
/// The built-in generator emits <c>{"enum":["Male","Female","Undetermined"]}</c>
/// with no <c>type</c>. That is legal OpenAPI 3.1 -- JSON Schema 2020-12 lets
/// the type be inferred from the values -- and Swashbuckle wrote
/// <c>{"type":"string","enum":[...]}</c>. The difference is invisible until a
/// client generator that does not do the inference turns `sex` into `unknown`
/// or `any`, at which point the one field a civil registry most needs typed
/// is the one that is not.
///
/// Costs a line in the document and removes the dependency on how a
/// particular generator reads an untyped enum.
/// </summary>
public class EnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Only where the type is genuinely absent, and only where every value
        // really is a string -- an integer enum left untyped should stay that
        // way rather than be relabelled into something it is not.
        if (schema.Type is not null || schema.Enum is not { Count: > 0 })
        {
            return Task.CompletedTask;
        }

        // A nullable enum has null folded into its values, which is how 3.1
        // expresses it. The null has to be carried into the type as well, or
        // the schema would say the value is always a string while its own
        // enum offers null.
        //
        // The distinction is load-bearing for the statistics questionnaire:
        // NotStated is a real answer and null means the question was never
        // put (draft 6.5.1). A client that cannot tell them apart aggregates
        // "never asked" into "declined to say".
        var nullable = false;

        foreach (var value in schema.Enum)
        {
            if (value is null || value.GetValueKind() == JsonValueKind.Null)
            {
                nullable = true;
            }
            else if (value is not JsonValue node || !node.TryGetValue<string>(out _))
            {
                return Task.CompletedTask;
            }
        }

        schema.Type = nullable
            ? JsonSchemaType.String | JsonSchemaType.Null
            : JsonSchemaType.String;

        return Task.CompletedTask;
    }
}
