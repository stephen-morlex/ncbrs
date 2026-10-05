using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The composed BRN, <c>SS-OFFICE-YEAR-RUNNING-CHECK</c>. What these pin: a
/// number round-trips; a misread or mistyped character, or two neighbours
/// swapped, is read as mistyped and never as another valid number; and a
/// number from before the format is still a number.
/// </summary>
public class BrnFormatTests
{
    private const string Alphanumerics = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    [Theory]
    [InlineData("JTH01", 2026, 123)]
    [InlineData("RJ", 2026, 1)]
    [InlineData("ABCDEF", 2031, 999_999)]
    public void ANumberRoundTrips(string office, int year, long running)
    {
        var brn = BrnFormat.Compose(office, year, running);

        Assert.Matches(@"^SS-[A-Z0-9]{2,6}-\d{4}-\d{6}-[A-Z0-9]$", brn);
        Assert.Equal(BrnReading.Composed, BrnFormat.Read(brn, out var parts));
        Assert.Equal(new BrnParts(office, year, running), parts);
        Assert.Equal(brn, parts!.Composed);
    }

    [Fact]
    public void ItIsReadAsTypedAtACounter_InLowerCaseOrWithSpaces()
    {
        var brn = BrnFormat.Compose("JTH01", 2026, 123);

        Assert.Equal(BrnReading.Composed, BrnFormat.Read(brn.ToLowerInvariant(), out _));
        Assert.Equal(BrnReading.Composed, BrnFormat.Read(" " + brn.Replace("-2026-", "- 2026 -") + " ", out _));
    }

    /// <summary>A missing dash is not supplied: office codes vary in length, and a guess could land on another office.</summary>
    [Fact]
    public void AMissingDashIsNotGuessed()
    {
        var brn = BrnFormat.Compose("JTH01", 2026, 123);

        Assert.Equal(BrnReading.Unrecognised, BrnFormat.Read(brn.Replace("-2026", "2026"), out _));
    }

    [Fact]
    public void EverySingleCharacterChangedIsCaught()
    {
        foreach (var brn in Samples())
        {
            for (var i = 0; i < brn.Length; i++)
            {
                if (brn[i] == '-')
                {
                    continue;
                }

                foreach (var other in Alphanumerics.Where(c => c != brn[i]))
                {
                    var changed = brn[..i] + other + brn[(i + 1)..];

                    Assert.True(BrnFormat.Read(changed, out _) != BrnReading.Composed,
                        $"{brn} with position {i} changed to '{other}' read as a valid number: {changed}");
                }
            }
        }
    }

    [Fact]
    public void EveryPairOfNeighboursSwappedIsCaught()
    {
        foreach (var brn in Samples())
        {
            // Neighbours as a person reads them: across a dash too.
            var positions = Enumerable.Range(0, brn.Length).Where(i => brn[i] != '-').ToList();

            for (var k = 0; k + 1 < positions.Count; k++)
            {
                var (a, b) = (positions[k], positions[k + 1]);
                if (brn[a] == brn[b])
                {
                    continue;
                }

                var swapped = brn.ToCharArray();
                (swapped[a], swapped[b]) = (swapped[b], swapped[a]);

                Assert.True(BrnFormat.Read(new string(swapped), out _) != BrnReading.Composed,
                    $"{brn} with positions {a} and {b} swapped read as a valid number: {new string(swapped)}");
            }
        }
    }

    [Fact]
    public void AChangedCheckCharacterReadsAsMistyped_NotAsUnknown()
    {
        var brn = BrnFormat.Compose("JTH01", 2026, 123);
        var wrong = brn[..^1] + (brn[^1] == 'A' ? 'B' : 'A');

        Assert.Equal(BrnReading.Mistyped, BrnFormat.Read(wrong, out var parts));
        Assert.Null(parts);
    }

    [Theory]
    [InlineData("100104", BrnReading.Legacy)]
    [InlineData("PROV-TAB-1-0001", BrnReading.Unrecognised)]
    [InlineData("", BrnReading.Unrecognised)]
    [InlineData("SS-J-2026-000123-K", BrnReading.Unrecognised)]
    [InlineData("SS-TOOLONG-2026-000123-K", BrnReading.Unrecognised)]
    public void OtherTextIsReadForWhatItIs(string text, BrnReading expected)
        => Assert.Equal(expected, BrnFormat.Read(text, out _));

    [Fact]
    public void RunningNumberZeroWasNeverIssued()
    {
        var body = "SS-JTH01-2026-000000";
        Assert.Equal(BrnReading.Unrecognised, BrnFormat.Read($"{body}-{BrnFormat.CheckCharacter(body)}", out _));
    }

    [Theory]
    [InlineData("jth01")]
    [InlineData("J")]
    [InlineData("JTH-01")]
    [InlineData("JTH0123")]
    public void AnOfficeCodeIsTwoToSixCapitalsOrDigits(string code)
    {
        Assert.False(BrnFormat.IsOfficeCode(code));
        Assert.Throws<ArgumentException>(() => BrnFormat.Compose(code, 2026, 1));
    }

    [Fact]
    public void TheRunningNumberIsOneToNineHundredNinetyNineThousand()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BrnFormat.Compose("JTH01", 2026, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrnFormat.Compose("JTH01", 2026, 1_000_000));
    }

    private static IEnumerable<string> Samples() =>
    [
        BrnFormat.Compose("JTH01", 2026, 123),
        BrnFormat.Compose("ZZ", 2029, 909_090),
        BrnFormat.Compose("A0Z9", 2026, 1),
        BrnFormat.Compose("TRK2", 2027, 450_017),
    ];
}
