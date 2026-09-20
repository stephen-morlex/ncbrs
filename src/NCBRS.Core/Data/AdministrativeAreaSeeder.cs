using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NCBRS.Models;

namespace NCBRS.Data;

/// <summary>
/// Seeds South Sudan's administrative geography from the committed data file
/// (<c>SouthSudanAreas.json</c>), idempotently: an area is inserted only if its
/// <see cref="AdministrativeArea.Code"/> is not already present, so it is safe
/// to run on every startup and safe to re-run after the data file is corrected.
///
/// Only the country, states and counties are seeded. The three administrative
/// areas (Abyei, Greater Pibor, Ruweng) are seeded at the state tier, being
/// state-equivalent. Payam/Block/Boma/Quarter/Village are never seeded — the
/// file does not carry them and the draft's rule is that they are created as
/// they are encountered, not invented ahead of a birth.
///
/// The county list in the data file is explicitly unverified (see its
/// <c>provenance</c>): correct it there and re-run, rather than hard-coding
/// places here.
/// </summary>
public static class AdministrativeAreaSeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task SeedAsync(NcbrsDbContext db, CancellationToken cancellationToken = default)
    {
        var data = Load();

        var byCode = await db.AdministrativeAreas.ToDictionaryAsync(area => area.Code, area => area, cancellationToken);

        AdministrativeArea Ensure(string code, string name, AdministrativeLevel level, string? parentCode)
        {
            if (byCode.TryGetValue(code, out var existing))
            {
                return existing;
            }

            var area = new AdministrativeArea
            {
                Name = name,
                Level = level,
                Code = code,
                ParentId = parentCode is null ? null : byCode[parentCode].AdministrativeAreaId,
            };

            db.AdministrativeAreas.Add(area);
            byCode[code] = area;
            return area;
        }

        Ensure(data.Country.Code, data.Country.Name, AdministrativeLevel.Country, parentCode: null);

        foreach (var state in data.States)
        {
            Ensure(state.Code, state.Name, AdministrativeLevel.State, data.Country.Code);

            foreach (var county in state.Counties)
            {
                Ensure(county.Code, county.Name, AdministrativeLevel.County, state.Code);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static SeedData Load()
    {
        var assembly = typeof(AdministrativeAreaSeeder).Assembly;

        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("SouthSudanAreas.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The South Sudan seed data resource was not found in the assembly.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;

        return JsonSerializer.Deserialize<SeedData>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The South Sudan seed data was empty or malformed.");
    }

    private sealed record SeedData(AreaNode Country, IReadOnlyList<StateNode> States);

    private sealed record StateNode(string Code, string Name, bool AdministrativeArea, IReadOnlyList<AreaNode> Counties);

    private sealed record AreaNode(string Code, string Name);
}
