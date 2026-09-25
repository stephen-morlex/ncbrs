using NCBRS.Models;

namespace NCBRS.LoadTest;

/// <summary>
/// The parts of a synthetic birth that the duplicate matcher reads, generated
/// so that no two load records look like the same child.
///
/// That is a requirement, not a nicety. A record the matcher flags writes
/// <c>DuplicateCandidates</c> rows, so load that clusters measures the cost of
/// a flood of review work that real registrations do not produce, and leaves
/// that flood in the review queue afterwards. The first version of this driver
/// did exactly that. It drew the first name from a pool of ten by <c>n % 10</c>,
/// the birth date by <c>n % 80</c> and the sex by <c>n &amp; 1</c>, and because
/// 80 is a multiple of both, every record born on a given day shared a first
/// name *and* a sex. Behind a shared first name as long as "Nyandeng", any two
/// six-letter surnames score at least 60% similar (at most 6 edits in 15
/// characters), so every same-day pair was flagged: 113,896 candidates from
/// 12,321 records, and an A7 per-record figure inflated several-fold (plan §17
/// 11d).
///
/// So: no part of a name is shared between records, and nothing the matcher
/// weighs is derived from the same counter as anything else. Two independent
/// random tokens of <see cref="TokenLength"/> letters are 17 characters with no
/// common structure, and scoring 60% would need them within 6 edits of each
/// other. <c>SyntheticBirthsTests</c> holds that against the real matcher at
/// the worst case: same birth date, same sex, same facility.
/// </summary>
public static class SyntheticBirths
{
    public const int TokenLength = 8;

    /// <summary>Two independent random tokens, capitalised like a name.</summary>
    public static string ChildName(Random random) => $"{Token(random)} {Token(random)}";

    /// <summary>Chosen independently of the date and the name.</summary>
    public static Sex ChildSex(Random random) => random.Next(2) == 0 ? Sex.Female : Sex.Male;

    private static string Token(Random random)
        => string.Create(TokenLength, random, static (span, rng) =>
        {
            span[0] = (char)('A' + rng.Next(26));

            for (var k = 1; k < span.Length; k++)
            {
                span[k] = (char)('a' + rng.Next(26));
            }
        });
}
