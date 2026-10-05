using System.Globalization;
using System.Text.RegularExpressions;

namespace NCBRS.Models;

/// <summary>What a piece of text is, read as a birth registration number.</summary>
public enum BrnReading
{
    /// <summary>A composed number whose check character agrees: <c>SS-JTH01-2026-000123-K</c>.</summary>
    Composed,

    /// <summary>
    /// Shaped like a composed number, but its check character disagrees with
    /// the rest: one character was misread or mistyped, or two were swapped.
    /// </summary>
    Mistyped,

    /// <summary>A number from before the composed format: digits only, drawn from a numeric range.</summary>
    Legacy,

    /// <summary>Neither — including a provisional identifier, which is deliberately not a BRN.</summary>
    Unrecognised,
}

/// <summary>The parts of a composed number.</summary>
public sealed record BrnParts(string OfficeCode, int Year, long Running)
{
    public string Composed => BrnFormat.Compose(OfficeCode, Year, Running);
}

/// <summary>
/// The birth registration number, decided 2026-10-02:
/// <c>SS-&lt;OFFICE&gt;-&lt;YEAR&gt;-&lt;RUNNING&gt;-&lt;CHECK&gt;</c>, for example
/// <c>SS-JTH01-2026-000123-K</c>.
///
/// - **The office is the registering facility**, by a code the Ministry gives it
///   once (<see cref="Facility.OfficeCode"/>). Issued numbers carry it, so it is
///   never changed.
/// - **The year is the year the number was issued**, from the block it was
///   drawn from — not the year of the birth. A child registered late is still
///   numbered in the year the registry numbered them, and a block granted in
///   December carries on after New Year rather than being renumbered.
/// - **The running number counts from 1 per facility per year**, six digits.
/// - **The check character is ISO 7064 MOD 37,36** over every letter and digit
///   before it. It catches every single character misread or mistyped and
///   every pair of neighbours swapped. (Luhn mod 36, first proposed, misses a
///   swap of 0 and Z.) A number is read off paper, typed at a counter and
///   spoken over a phone; a mistyped one should be caught as mistyped, never
///   resolve to somebody else's registration.
///
/// One copy, here in Contracts, because the tablet composes the numbers it
/// issues offline and the registry checks them: a second copy of the rule is a
/// second place for it to drift.
///
/// Numbers issued before the format — digits only — stay valid and are never
/// renumbered (<see cref="BrnReading.Legacy"/>).
/// </summary>
public static partial class BrnFormat
{
    public const string Country = "SS";

    public const long MaxRunning = 999_999;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    [GeneratedRegex(@"^SS-([A-Z0-9]{2,6})-(\d{4})-(\d{6})-([A-Z0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    [GeneratedRegex(@"^[A-Z0-9]{2,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex OfficeShape();

    [GeneratedRegex(@"^\d{1,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyShape();

    /// <summary>Two to six capital letters or digits.</summary>
    public static bool IsOfficeCode(string? code) => code is not null && OfficeShape().IsMatch(code);

    public static string Compose(string officeCode, int year, long running)
    {
        if (!IsOfficeCode(officeCode))
        {
            throw new ArgumentException($"'{officeCode}' is not an office code: two to six capital letters or digits.", nameof(officeCode));
        }

        if (year is < 1000 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(year), year, "The year is four digits.");
        }

        if (running is < 1 or > MaxRunning)
        {
            throw new ArgumentOutOfRangeException(nameof(running), running, $"The running number is 1 to {MaxRunning}.");
        }

        var body = string.Create(CultureInfo.InvariantCulture, $"{Country}-{officeCode}-{year:D4}-{running:D6}");
        return $"{body}-{CheckCharacter(body)}";
    }

    /// <summary>
    /// The number as it is stored: capitals, no spaces. Typed at a counter it
    /// may arrive in lower case or with a space where the paper had a line
    /// break. A missing dash is not supplied, because office codes vary in
    /// length and a guess could land on another office's number.
    /// </summary>
    public static string Normalise(string? text)
        => new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    /// <summary>Reads <paramref name="text"/> as a BRN, normalising it first.</summary>
    public static BrnReading Read(string? text, out BrnParts? parts)
    {
        parts = null;
        var normalised = Normalise(text);

        var match = Shape().Match(normalised);
        if (match.Success)
        {
            if (!HasValidCheck(normalised))
            {
                return BrnReading.Mistyped;
            }

            parts = new BrnParts(
                match.Groups[1].Value,
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));

            // A running number of zero is shaped right and checks, but was
            // never issued: running numbers start at 1.
            return parts.Running >= 1 ? BrnReading.Composed : BrnReading.Unrecognised;
        }

        return LegacyShape().IsMatch(normalised) ? BrnReading.Legacy : BrnReading.Unrecognised;
    }

    /// <summary>The check character for everything before it (dashes are not counted).</summary>
    public static char CheckCharacter(string body)
    {
        var p = Mod37Over36(body, out _);
        return Alphabet[(37 - p) % 36];
    }

    private static bool HasValidCheck(string composed)
    {
        Mod37Over36(composed, out var lastSum);
        return lastSum == 1;
    }

    /// <summary>
    /// ISO 7064 MOD 37,36 (hybrid system, M = 36): for each character, add its
    /// value, reduce mod 36 (0 becomes 36), double, reduce mod 37. Over a
    /// number including its check character, the last reduced sum is 1.
    /// </summary>
    private static int Mod37Over36(string text, out int lastSum)
    {
        var p = 36;
        lastSum = 0;

        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                continue;
            }

            var s = (p + value) % 36;
            if (s == 0)
            {
                s = 36;
            }

            lastSum = s;
            p = s * 2 % 37;
        }

        return p;
    }
}
