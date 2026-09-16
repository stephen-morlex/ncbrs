using System.Text.Json;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Neither service's contract claims a number might arrive as a string.
///
/// ASP.NET's web JSON defaults set <c>JsonNumberHandling.AllowReadingFromString</c>,
/// and the built-in OpenAPI generator reports that faithfully as
/// <c>{"type":["integer","string"]}</c>. Because one component schema
/// describes both directions, every number a service *returns* inherits it —
/// so every count, total and median types as <c>number | string</c> in the
/// generated client, and every arithmetic use of one needs a cast that exists
/// only because the document overstated what comes back.
///
/// **This is pinned by a test rather than left to the contract diff because it
/// has already been rationalised once in this repo rather than diagnosed.**
/// The CI drift check fails on any contract change, which catches a
/// regression only if whoever reads the diff recognises the widening as a
/// fault; it looks like a harmless generator detail, and once it did.
///
/// The two services close it differently and both are deliberate. The API
/// keeps the leniency and corrects the document with NumberSchemaTransformer,
/// because it takes request bodies and chooses to be forgiving about them.
/// The consumer has no request bodies at all — every endpoint is a GET — so it
/// simply is not lenient, and its document states what it does rather than
/// describing a leniency away.
/// </summary>
public class OpenApiNumberTypingTests
{
    [Theory]
    [InlineData("NCBRS.Api.json")]
    [InlineData("NCBRS.Consumer.json")]
    public void NoSchemaSaysANumberMightBeAString(string document)
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(PathTo(document)));

        var offenders = new List<string>();

        Walk(contract.RootElement, "", offenders);

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Every schema in the document, by path, flagging any whose type list
    /// contains both a numeric type and "string". Null alongside a number is
    /// fine and expected — a median with no inputs reports null rather than a
    /// misleading zero, and that has to survive into the contract.
    /// </summary>
    private static void Walk(JsonElement node, string path, List<string> offenders)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("type", out var type) && IsNumberOrString(type))
                {
                    offenders.Add(path);
                }

                foreach (var property in node.EnumerateObject())
                {
                    Walk(property.Value, $"{path}/{property.Name}", offenders);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;

                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, $"{path}/{index++}", offenders);
                }

                break;
        }
    }

    private static bool IsNumberOrString(JsonElement type)
    {
        if (type.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        var names = type.EnumerateArray()
            .Where(entry => entry.ValueKind is JsonValueKind.String)
            .Select(entry => entry.GetString())
            .ToList();

        return names.Contains("string")
               && (names.Contains("integer") || names.Contains("number"));
    }

    /// <summary>
    /// The committed documents live at the repository root, which is several
    /// levels above the test assembly and at a different depth per
    /// configuration. Walking up to a known marker beats hard-coding
    /// <c>../../../../..</c> and silently reading nothing when the layout
    /// changes.
    /// </summary>
    private static string PathTo(string document)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NCBRS.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory!.FullName, "web", "openapi", document);

        // Asserted rather than skipped. A contract test that quietly passes
        // when it cannot find the contract is worse than no test: it reports
        // green for a property nobody checked.
        Assert.True(File.Exists(path), $"The committed contract is missing: {path}");

        return path;
    }
}
