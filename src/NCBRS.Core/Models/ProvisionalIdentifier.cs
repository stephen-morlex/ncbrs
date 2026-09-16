using System.Text.RegularExpressions;

namespace NCBRS.Models;

/// <summary>
/// The fallback identifier a device issues when it exhausts its BRN block
/// while offline (draft 6.3).
///
/// This is the one place the system knowingly departs from "no placeholder
/// IDs, no renumbering later". The departure is narrower than it looks: a
/// provisional identifier is deliberately <em>not</em> a BRN and never
/// pretends to be one, so assigning a real BRN at reconciliation is
/// numbering the record for the first time rather than renumbering it. That
/// is why the prefix is loud and the format is unmistakable — anyone holding
/// a slip with PROV- on it can see it is not yet a registration number.
///
/// The alternative is worse in every direction. A device that stops
/// registering when its block runs out sends families away from the only
/// health worker they may see for weeks; one that keeps issuing numbers past
/// the end of its block collides with a block granted to someone else, and
/// nobody finds out for years.
/// </summary>
public static partial class ProvisionalIdentifier
{
    public const string Prefix = "PROV-";

    /// <summary>
    /// PROV-{device}-{sequence}. The device segment is what keeps two posts
    /// that both run dry in the same week from colliding; the sequence is
    /// local to the device and need only be unique within it.
    /// </summary>
    [GeneratedRegex(@"^PROV-[A-Za-z0-9._-]{1,40}-\d{1,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool Looks(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool IsWellFormed(string? value)
        => !string.IsNullOrWhiteSpace(value) && Pattern().IsMatch(value);

    public static string For(string deviceId, long sequence)
        => $"{Prefix}{deviceId}-{sequence}";
}
