using System.Text.Json;
using NCBRS.Data;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The DHIS2 configuration that ships with the Consumer.
///
/// The export groups by county p-code (<c>SS0101</c>). After the county rename
/// the committed org-unit map was still keyed by the old district codes
/// (<c>SS-CE-JUB</c>), so the dev export reported every county unmapped and
/// published nothing, for every period, with no test noticing: an unmapped
/// county is reported rather than failed, which is right for production and
/// made the drift invisible here.
///
/// The placeholder UIDs sat in the base settings too, which every environment
/// loads, so production would have filed births against data elements that do
/// not exist in its DHIS2 — which DHIS2 shows as a period with no births.
/// </summary>
public class Dhis2ExportSettingsTests
{
    private static readonly string[] ElementKeys =
    [
        "LiveBirths", "LiveBirthsMale", "LiveBirthsFemale", "FetalDeaths",
        "NeonatalDeaths", "MaternalDeaths", "RegisteredWithinWindow",
    ];

    /// <summary>
    /// UIDs and org units are instance-specific. An unconfigured element is
    /// skipped and an unmapped county is reported; a placeholder is sent.
    /// </summary>
    [Fact]
    public void TheBaseSettingsNameNoDhis2Instance()
    {
        var settings = ShippedSettings.Read("NCBRS.Consumer", "appsettings.json");

        Assert.All(ElementKeys, key => Assert.False(
            ShippedSettings.Sets(settings, "Dhis2Export", key, out _),
            $"Dhis2Export:{key} is set in the base settings, so it ships to production."));
        Assert.False(ShippedSettings.Sets(settings, "Dhis2Export", "OrgUnits", out _));
    }

    [Fact]
    public void TheDevelopmentMapCoversEveryCountyTheSeedUses()
    {
        var settings = ShippedSettings.Read("NCBRS.Consumer", "appsettings.Development.json");
        Assert.True(ShippedSettings.Sets(settings, "Dhis2Export", "OrgUnits", out var orgUnits));

        var mapped = orgUnits.EnumerateObject().Select(unit => unit.Name).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(DevelopmentDataSeeder.SeededCountyCodes);
        Assert.All(DevelopmentDataSeeder.SeededCountyCodes, county => Assert.Contains(county, mapped));
    }

    [Fact]
    public void TheDevelopmentSettingsConfigureEveryElement()
    {
        var settings = ShippedSettings.Read("NCBRS.Consumer", "appsettings.Development.json");

        Assert.All(ElementKeys, key => Assert.True(
            ShippedSettings.Sets(settings, "Dhis2Export", key, out var uid)
            && uid.ValueKind == JsonValueKind.String && uid.GetString() is { Length: > 0 },
            $"Dhis2Export:{key} is not configured for Development, so the dev export skips it."));
    }
}
