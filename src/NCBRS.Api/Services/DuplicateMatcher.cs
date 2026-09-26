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

        var normalisedLeft = Normalise(left);
        var normalisedRight = Normalise(right);

        // Two names that disagree on a whole word are two people, however
        // alike the rest of the string is. Checked before the whole-string
        // similarity, which cannot tell a misspelt word from a different one.
        var words = CompareWords(normalisedLeft, normalisedRight);
        if (words.DisagreeOnAWord)
        {
            return 0;
        }

        // The better of the whole string and the word-by-word alignment. The
        // whole string alone cannot see that "Deng Ayen" is "Ayen Deng" with
        // the words the other way round -- registrars do not agree on which
        // comes first -- and taking the better of the two loses no match the
        // whole string found before.
        var similarity = Math.Max(Similarity(normalisedLeft, normalisedRight), words.AlignedSimilarity);

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

    /// <summary>
    /// A word must be at least this alike to its counterpart to count as the
    /// same word spelt differently. A misspelling keeps most of a word --
    /// Deng/Deeng 80%, Lado/Ladu 75%, Nyandeng/Nyandheng 89% -- while two
    /// different names in the same families of names fall well short:
    /// Achol/Aluel 40%, Malith/Gatwech 29%, Deng/Garang 33%.
    /// </summary>
    private const double WordSimilarityFloor = 0.5;

    /// <summary>
    /// Words this short are initials or particles ("A", "de"): too little to
    /// say two names differ on.
    /// </summary>
    private const int ShortestComparableWord = 3;

    /// <summary>At most 720 alignments; a name longer than this is not a name.</summary>
    private const int MostWordsAligned = 6;

    /// <summary>
    /// Whether the two names disagree on a whole word (plan §17 11e), and how
    /// alike they are word by word.
    ///
    /// Whole-string edit distance rewards any shared word, and in this
    /// registry sharing one is routine: a name is a given name and the father's
    /// name, so siblings, cousins and a county's worth of Dengs share one, and
    /// the common given names -- Nyandeng, Ayen, Deng -- recur constantly. So
    /// "Nyandeng Deng" and "Nyandeng Garang", same day, same sex, same
    /// hospital, scored 63 and went to review; mothers "Achol Deng" and "Aluel
    /// Deng" scored a 70% "close match". Every duplicate in the demo seed was
    /// that and nothing else: different people sharing one word.
    ///
    /// The words of the shorter name are aligned one to one with the longer
    /// name's, in whatever order matches best -- word order varies between
    /// registrars, and a name recorded without one of its words is common --
    /// and if any aligned pair of real words falls under
    /// <see cref="WordSimilarityFloor"/>, the names differ. A missing word or
    /// a swapped order is not a difference; a different word is.
    ///
    /// This needs no data about how common a name is, which is why it could be
    /// done now. Weighting a shared *rare* word above a shared common one is
    /// the next refinement, and does need real name-frequency data.
    /// </summary>
    private static (bool DisagreeOnAWord, double AlignedSimilarity) CompareWords(string left, string right)
    {
        var leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightWords = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var (shorter, longer) = leftWords.Length <= rightWords.Length
            ? (leftWords, rightWords)
            : (rightWords, leftWords);

        // Nothing to align, or more words than any real name has -- where
        // trying every alignment would cost more than it could tell us. Either
        // way, fall back to the whole-string comparison alone.
        if (shorter.Length == 0 || longer.Length > MostWordsAligned)
        {
            return (false, 0);
        }

        // Names are a handful of words, so trying every alignment is cheap
        // and, unlike a greedy match, cannot pair the wrong words.
        var best = BestAlignment(shorter, longer, 0, new bool[longer.Length]);

        var disagree = best.Any(pair =>
            Math.Min(pair.Left.Length, pair.Right.Length) >= ShortestComparableWord
            && Similarity(pair.Left, pair.Right) < WordSimilarityFloor);

        // Weighted by length, over every word of the longer name: a long word
        // matched counts for more than an initial, and a word missing from the
        // shorter name counts for nothing -- incomplete, not identical.
        var matched = best.Sum(pair => Similarity(pair.Left, pair.Right) * Math.Max(pair.Left.Length, pair.Right.Length));
        var total = longer.Sum(word => word.Length) + best.Sum(pair => Math.Max(0, pair.Left.Length - pair.Right.Length));

        return (disagree, total == 0 ? 0 : matched / total);
    }

    /// <summary>
    /// The one-to-one pairing of <paramref name="shorter"/>'s words with
    /// <paramref name="longer"/>'s that maximises total similarity.
    /// </summary>
    private static List<(string Left, string Right)> BestAlignment(
        string[] shorter, string[] longer, int index, bool[] used)
    {
        if (index == shorter.Length)
        {
            return [];
        }

        List<(string Left, string Right)>? best = null;
        var bestScore = double.MinValue;

        for (var j = 0; j < longer.Length; j++)
        {
            if (used[j])
            {
                continue;
            }

            used[j] = true;
            var rest = BestAlignment(shorter, longer, index + 1, used);
            used[j] = false;

            var candidate = new List<(string Left, string Right)> { (shorter[index], longer[j]) };
            candidate.AddRange(rest);

            var score = candidate.Sum(pair => Similarity(pair.Left, pair.Right));
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best!;
    }

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
