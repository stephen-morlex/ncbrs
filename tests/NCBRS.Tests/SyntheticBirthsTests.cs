using NCBRS.LoadTest;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The load driver's synthetic births must not look like duplicates of each
/// other to the real matcher (plan §17 11d). When they did, an A7 run measured
/// a flood of review work that real registrations do not produce -- 113,896
/// candidates from 12,321 records -- and left it in the review queue.
///
/// Tested at the matcher's worst case rather than the driver's typical one:
/// every pair shares a birth date, a sex and a facility, so the name is the
/// only thing keeping a pair under the review threshold.
/// </summary>
public class SyntheticBirthsTests
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly DateTime SameDay = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BirthRecord Birth(string name, int brn) => new()
    {
        Brn = brn.ToString(),
        ChildPerson = new Person { FullName = name },
        FacilityId = FacilityId,
        DateOfBirth = SameDay,
        Sex = Sex.Female,
        Plurality = BirthPlurality.Singleton,
        BirthOrder = 1,
    };

    private static int WorstScore(IReadOnlyList<string> names)
    {
        var matcher = new DuplicateMatcher();
        var births = names.Select((name, i) => Birth(name, 100_000 + i)).ToList();
        var worst = 0;

        for (var i = 0; i < births.Count; i++)
        {
            for (var j = i + 1; j < births.Count; j++)
            {
                worst = Math.Max(worst, matcher.Assess(births[i], births[j]).Score);
            }
        }

        return worst;
    }

    /// <summary>
    /// 400 names is 79,800 pairs -- far more same-day, same-sex pairs than a
    /// facility's load run produces for any one date. Fixed seed, so a failure
    /// reproduces.
    /// </summary>
    [Fact]
    public void NoTwoSyntheticNamesLookLikeTheSameChild()
    {
        var random = new Random(20260925);
        var names = Enumerable.Range(0, 400).Select(_ => SyntheticBirths.ChildName(random)).ToList();

        Assert.True(WorstScore(names) < DuplicateMatcher.ReviewThreshold,
            "synthetic load records would be flagged as duplicates of each other");
    }

    /// <summary>
    /// The control: two names one letter apart, under the same conditions,
    /// must be flagged -- otherwise the test above would pass because the
    /// harness could not detect clustering at all, not because the names are
    /// distinct.
    ///
    /// (The first driver's own names -- "Nyandeng QXJWPZ" against "Nyandeng
    /// BRTKLM" -- were the original control. They are no longer flagged, and
    /// rightly: since §17 11e the matcher treats names that disagree on a whole
    /// word as different people. The driver still must not rely on that.)
    /// </summary>
    [Fact]
    public void NamesOneLetterApartAreFlagged()
    {
        Assert.True(WorstScore(["Nyandeng Qxjwpz", "Nyandeng Qxjwpa"]) >= DuplicateMatcher.ReviewThreshold);
    }

    [Fact]
    public void SexIsNotFixedByAnythingElse()
    {
        var random = new Random(7);
        var sexes = Enumerable.Range(0, 200).Select(_ => SyntheticBirths.ChildSex(random)).ToList();

        Assert.Contains(Sex.Female, sexes);
        Assert.Contains(Sex.Male, sexes);
    }
}
