using NCBRS.Models;

namespace NCBRS.Services;

public record MatchAssessment(int Score, IReadOnlyList<string> Reasons)
{
    public static readonly MatchAssessment NoMatch = new(0, []);
}

/// <summary>
/// Scores how likely two birth records are to be the same child.
///
/// This is deliberately a plain, explainable heuristic rather than a
/// probabilistic record-linkage model: a registrar has to be able to look at
/// the reasons and judge, and "the model said 0.87" is not something anyone
/// can defend when a citizen disputes it.
///
/// Levenshtein on normalised names rather than a phonetic algorithm --
/// Soundex and its relatives are tuned for English and behave poorly on the
/// naming conventions this registry actually holds. A production deployment
/// should revisit this against real national name data.
/// </summary>
public class DuplicateMatcher
{
    /// <summary>At or above this, a candidate is recorded for review.</summary>
    public const int ReviewThreshold = 60;

    /// <summary>
    /// How far apart two recorded birth dates can be and still plausibly be
    /// the same birth. Wider than zero because the second registration is
    /// often taken from a parent's recollection rather than a record.
    /// </summary>
    public const int DateWindowDays = 3;

    public MatchAssessment Assess(BirthRecord candidate, BirthRecord existing)
    {
        if (candidate.BirthRecordId == existing.BirthRecordId)
        {
            return MatchAssessment.NoMatch;
        }

        // Siblings, not duplicates. Twins share a mother, a date and often a
        // sex; without this the matcher would flag every multiple birth in
        // the country. Birth order is exactly what distinguishes them, which
        // is why the model records it per sibling.
        if (AreSiblings(candidate, existing))
        {
            return MatchAssessment.NoMatch;
        }

        var reasons = new List<string>();
        var score = 0;

        var daysApart = Math.Abs(
            (candidate.DateOfBirth.Date - existing.DateOfBirth.Date).Days);

        if (daysApart > DateWindowDays)
        {
            return MatchAssessment.NoMatch;
        }

        switch (daysApart)
        {
            case 0:
                score += 35;
                reasons.Add("Same date of birth.");
                break;
            case 1:
                score += 22;
                reasons.Add("Dates of birth one day apart.");
                break;
            default:
                score += 12;
                reasons.Add($"Dates of birth {daysApart} days apart.");
                break;
        }

        if (candidate.Sex != existing.Sex)
        {
            // Not impossible -- a sex can be recorded wrongly -- but it
            // argues strongly against these being one child.
            score -= 30;
            reasons.Add("Recorded sex differs.");
        }
        else
        {
            score += 10;
            reasons.Add("Same sex.");
        }

        score += NameScore(
            candidate.ChildPerson?.FullName,
            existing.ChildPerson?.FullName,
            weight: 25,
            label: "Child name",
            reasons);

        score += NameScore(
            candidate.MotherPerson?.FullName,
            existing.MotherPerson?.FullName,
            weight: 35,
            label: "Mother name",
            reasons);

        // The case this feature exists for: the same birth entering the
        // registry twice through two different facilities.
        if (candidate.FacilityId != existing.FacilityId)
        {
            score += 10;
            reasons.Add("Registered at a different facility.");
        }

        return new MatchAssessment(Math.Clamp(score, 0, 100), reasons);
    }

    /// <summary>
    /// Two records of a multiple birth with different birth orders are
    /// siblings by definition, however alike everything else looks.
    /// </summary>
    private static bool AreSiblings(BirthRecord left, BirthRecord right)
        => left.Plurality != BirthPlurality.Singleton
           && right.Plurality != BirthPlurality.Singleton
           && left.BirthOrder is not null
           && right.BirthOrder is not null
           && left.BirthOrder != right.BirthOrder;

    private static int NameScore(
        string? left,
        string? right,
        int weight,
        string label,
        List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            // A child is often registered before being named, so an absent
            // name is uninformative rather than evidence either way.
            return 0;
        }

        var similarity = Similarity(Normalise(left), Normalise(right));

        if (similarity < 0.6)
        {
            return 0;
        }

        var awarded = (int)Math.Round(weight * similarity);

        reasons.Add(similarity >= 0.999
            ? $"{label} matches exactly."
            : $"{label} is a close match ({similarity:P0}).");

        return awarded;
    }

    /// <summary>
    /// Case, punctuation and spacing vary between a rushed village entry and
    /// a hospital one; none of it distinguishes two children.
    /// </summary>
    private static string Normalise(string value)
        => new string(value
                .Trim()
                .ToLowerInvariant()
                .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
                .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(string.Empty, (accumulated, part) =>
                accumulated.Length == 0 ? part : $"{accumulated} {part}");

    /// <summary>1.0 identical, 0.0 nothing in common.</summary>
    private static double Similarity(string left, string right)
    {
        if (left == right)
        {
            return 1.0;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return 0.0;
        }

        var distance = Levenshtein(left, right);
        return 1.0 - ((double)distance / Math.Max(left.Length, right.Length));
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
