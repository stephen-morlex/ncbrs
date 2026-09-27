using System.Text.Json;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The committed API contract says which endpoints need a token, and says it
/// truthfully. CI fails when the committed document is stale, so asserting on
/// it asserts on what the generated web client is built from.
///
/// Every endpoint sits behind the fallback policy except the few that opt out
/// with <c>[AllowAnonymous]</c>. The document used to put the bearer
/// requirement on all of them, anonymous ones included: certificate
/// verification, which anyone holding a certificate must be able to call, and
/// the monitor's <c>/health</c>.
/// </summary>
public class OpenApiSecurityTests
{
    private static JsonElement Document()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NCBRS.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "web", "openapi", "NCBRS.Api.json");
        Assert.True(File.Exists(path), $"The committed contract is missing: {path}");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static bool IsAnonymous(JsonElement document, string path, string method)
        => document.GetProperty("paths").GetProperty(path).GetProperty(method)
               .TryGetProperty("security", out var security)
           && security.GetArrayLength() == 0;

    /// <summary>Exactly the endpoints that opt out, and nothing else.</summary>
    [Fact]
    public void OnlyTheAnonymousEndpointsAreDocumentedAsNeedingNoToken()
    {
        var document = Document();

        var anonymous = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => IsAnonymous(document, path.Name, operation.Name))
                .Select(operation => $"{operation.Name.ToUpperInvariant()} {path.Name}"))
            .Order()
            .ToList();

        Assert.Equal(
            [
                "GET /api/certificates/offline-bundle",
                "GET /api/certificates/revocations",
                "GET /api/certificates/signing-key",
                "GET /health",
                "POST /api/certificates/verify",
            ],
            anonymous);
    }

    /// <summary>Everything else inherits the document's bearer requirement.</summary>
    [Fact]
    public void TheDocumentStillRequiresATokenByDefault()
    {
        var document = Document();

        Assert.Contains(
            document.GetProperty("security").EnumerateArray(),
            requirement => requirement.TryGetProperty("bearer", out _));
        Assert.False(IsAnonymous(document, "/api/BirthRecords/register", "post"));
    }
}
