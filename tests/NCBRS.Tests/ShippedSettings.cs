using System.Text.Json;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Reads a service's settings files as committed, so tests can pin what ships
/// in them. The base <c>appsettings.json</c> is loaded in every environment;
/// <c>appsettings.Development.json</c> only in Development.
/// </summary>
internal static class ShippedSettings
{
    public static JsonElement Read(string project, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NCBRS.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "src", project, file))).RootElement;
    }

    public static bool Sets(JsonElement settings, string section, string key, out JsonElement value)
    {
        value = default;
        return settings.TryGetProperty(section, out var node) && node.TryGetProperty(key, out value);
    }

    public static bool SetsFalse(JsonElement settings, string section, string key)
        => Sets(settings, section, key, out var value) && value.ValueKind == JsonValueKind.False;

    public static bool SetsTrue(JsonElement settings, string section, string key)
        => Sets(settings, section, key, out var value) && value.ValueKind == JsonValueKind.True;
}
