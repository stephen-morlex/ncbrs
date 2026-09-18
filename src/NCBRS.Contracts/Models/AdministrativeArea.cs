namespace NCBRS.Models;

/// <summary>
/// The levels of South Sudan's administrative hierarchy.
///
/// A county is divided into rural <see cref="Payam"/>s or urban
/// <see cref="Block"/>s, and those into <see cref="Boma"/>s or
/// <see cref="Quarter"/>s — so the same tier carries a rural and an urban
/// name, and a place is one or the other, not both. Not every branch reaches
/// the same depth, which is why the hierarchy is a tree with a level rather
/// than a fixed column per level.
/// </summary>
public enum AdministrativeLevel
{
    Country,
    State,
    County,

    /// <summary>Rural, under a county.</summary>
    Payam,

    /// <summary>Urban, under a county.</summary>
    Block,

    /// <summary>Rural, under a payam.</summary>
    Boma,

    /// <summary>Urban, under a block.</summary>
    Quarter,

    Village,
}

/// <summary>
/// A node in the administrative hierarchy — the country, a state, a county, or
/// a smaller area beneath one. One self-referencing table rather than a table
/// per level, because the levels vary in practice and forcing every location
/// to the same depth is exactly what the draft warns against: a county may be
/// split into payams or into blocks, and a village may be recorded directly
/// under a county where the intermediate area was never captured.
///
/// The <see cref="Level"/> says what a node is; <see cref="ParentId"/> says
/// where it sits. Scoping and reporting resolve a facility's county (and
/// state) by walking up the parent chain.
/// </summary>
public class AdministrativeArea
{
    public Guid AdministrativeAreaId { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public AdministrativeLevel Level { get; set; }

    /// <summary>Null only for the country root.</summary>
    public Guid? ParentId { get; set; }

    public AdministrativeArea? Parent { get; set; }

    /// <summary>
    /// A stable, human-meaningful code (for example a county's code), unique
    /// across the tree. It, not the surrogate id, is what audit rows and the
    /// reporting projection snapshot and what DHIS2 org units map to — so a
    /// name correction does not orphan a snapshot or a mapping.
    /// </summary>
    public required string Code { get; set; }
}

/// <summary>One area, as the read API returns it for dependent location pickers.</summary>
public record AdministrativeAreaResponse(
    Guid AdministrativeAreaId,
    string Name,
    AdministrativeLevel Level,
    string Code,
    Guid? ParentId);

/// <summary>
/// The rules of the hierarchy, kept dependency-light beside the model so the
/// device app can validate a tree offline exactly as the centre does.
/// </summary>
public static class AdministrativeLevels
{
    /// <summary>
    /// A node may sit under any strictly-higher tier, not only the one
    /// immediately above it. This is what keeps the hierarchy flexible: a
    /// village can hang directly off a county where no payam or boma was
    /// recorded. Payam and block share a tier (both under a county), as do
    /// boma and quarter.
    /// </summary>
    public static int Tier(AdministrativeLevel level) => level switch
    {
        AdministrativeLevel.Country => 0,
        AdministrativeLevel.State => 1,
        AdministrativeLevel.County => 2,
        AdministrativeLevel.Payam => 3,
        AdministrativeLevel.Block => 3,
        AdministrativeLevel.Boma => 4,
        AdministrativeLevel.Quarter => 4,
        AdministrativeLevel.Village => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown administrative level."),
    };

    public static bool IsRural(AdministrativeLevel level)
        => level is AdministrativeLevel.Payam or AdministrativeLevel.Boma;

    public static bool IsUrban(AdministrativeLevel level)
        => level is AdministrativeLevel.Block or AdministrativeLevel.Quarter;

    /// <summary>
    /// Whether <paramref name="parent"/> may contain <paramref name="child"/>:
    /// only when the parent is a strictly higher tier. The country contains
    /// everything below it; nothing contains the country.
    /// </summary>
    public static bool CanContain(AdministrativeLevel parent, AdministrativeLevel child)
        => Tier(parent) < Tier(child);

    /// <summary>
    /// The nearest ancestor at the given level, walking the loaded parent
    /// chain — how a facility's county or state is resolved. Returns null if
    /// no ancestor at that level is present (or the chain is not loaded).
    /// </summary>
    public static AdministrativeArea? AncestorOfLevel(AdministrativeArea area, AdministrativeLevel level)
    {
        for (AdministrativeArea? node = area; node is not null; node = node.Parent)
        {
            if (node.Level == level)
            {
                return node;
            }
        }

        return null;
    }
}
