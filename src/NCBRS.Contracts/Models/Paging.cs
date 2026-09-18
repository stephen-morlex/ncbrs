using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace NCBRS.Models;

/// <summary>
/// One page of a list endpoint (plan W7).
///
/// **Keyset, not offset, and the reason is the review queues.** Offset
/// pagination assumes the underlying set holds still. A review queue does the
/// opposite: working an item is what removes it. A district officer who loads
/// page 1, approves all ten corrections, then asks for page 2 has moved the
/// offset past ten items that shuffled down into the space the approved ones
/// left — so ten birth record corrections are skipped, silently, by a
/// reviewer who believes they have worked the whole queue.
///
/// A cursor is a position in the ordering rather than a count from the start,
/// so removing everything before it changes nothing. The cost is that there
/// is no "jump to page 7", which a queue worked oldest-first does not need.
/// </summary>
/// <param name="Total">
/// How many rows match in total, not just on this page — the backlog size a
/// district officer actually wants to know. Counted separately, so it is a
/// snapshot rather than a guarantee.
/// </param>
/// <param name="NextCursor">
/// Pass back as <c>after</c> for the following page. Null means this is the
/// last page.
/// </param>
public record Page<T>(IReadOnlyList<T> Items, int Total, string? NextCursor)
{
    /// <summary>
    /// Builds a page from a query that fetched one row more than the caller
    /// asked for. Whether that extra row exists is how we know a next page
    /// exists, without a second round trip.
    ///
    /// The cursor comes from the last row actually returned, never the probe
    /// row — a cursor pointing at a row the caller has not seen would skip it
    /// on the next page.
    /// </summary>
    public static Page<T> From(
        List<T> fetched, int total, int limit, Func<T, PageCursor> cursorOf)
    {
        if (fetched.Count <= limit)
        {
            return new Page<T>(fetched, total, NextCursor: null);
        }

        var items = fetched.Take(limit).ToList();

        return new Page<T>(items, total, cursorOf(items[^1]).Encode());
    }
}

/// <summary>
/// The paging parameters a list endpoint accepts.
/// </summary>
public record PageRequest
{
    /// <summary>
    /// Deliberately modest. A national register's queues can be large, and an
    /// unbounded list endpoint is a denial of service against the Ministry's
    /// own dashboard — which is what these endpoints were before W7.
    /// </summary>
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    public int Limit { get; init; } = DefaultLimit;

    /// <summary>Opaque. Clients pass back what they were given.</summary>
    public string? After { get; init; }

    /// <summary>
    /// Clamps rather than rejects an out-of-range limit. A client asking for
    /// 10,000 rows has made a judgement about its own needs, not an error
    /// worth failing a district officer's queue over — but it does not get
    /// 10,000 rows.
    /// </summary>
    public int EffectiveLimit => Limit switch
    {
        < 1 => DefaultLimit,
        > MaxLimit => MaxLimit,
        _ => Limit
    };
}

/// <summary>
/// A position in an ordered list: the value of whatever the list is sorted
/// by, plus the row's id.
///
/// The id breaks ties, and that is not a detail. Two corrections submitted in
/// the same millisecond, or two duplicate candidates with identical match
/// scores, would otherwise share a cursor — and a page boundary landing
/// between them either repeats one or skips one. On a review queue, skipping
/// one means a birth record nobody looks at.
///
/// The sort value is held as a string so one cursor serves every list: the
/// approval queues sort by time, the duplicate queue by match score, and a
/// cursor that only understood timestamps would have forced a second shape
/// for the second case.
///
/// Opaque to clients so the encoding can change, but deliberately **not**
/// signed. A forged cursor only moves a caller within data they are already
/// authorised to see, so signing would add key management for no gain.
/// </summary>
public readonly record struct PageCursor(string SortKey, Guid Id)
{
    public static PageCursor For(DateTime at, Guid id)
        => new(at.Ticks.ToString(CultureInfo.InvariantCulture), id);

    public static PageCursor For(int rank, Guid id)
        => new(rank.ToString(CultureInfo.InvariantCulture), id);

    public DateTime AsDateTime()
        => new(long.Parse(SortKey, CultureInfo.InvariantCulture), DateTimeKind.Utc);

    /// <summary>
    /// For lists ordered by an integer rank, such as a duplicate match score.
    /// Kept integral rather than widened to a double: equality against a
    /// floating-point parameter is the wrong comparison for an integer column,
    /// and on a review queue a mis-compared boundary row is one nobody sees.
    /// </summary>
    public int AsInt() => int.Parse(SortKey, CultureInfo.InvariantCulture);

    public string Encode()
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{SortKey}:{Id}"));

    /// <summary>
    /// A cursor this endpoint cannot read is refused rather than ignored.
    /// Silently returning the first page would show a reviewer the top of the
    /// queue while they believed they were three pages in.
    /// </summary>
    public static bool TryDecode(string? encoded, out PageCursor cursor)
    {
        cursor = default;

        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separator = decoded.LastIndexOf(':');

            if (separator <= 0 || !Guid.TryParse(decoded[(separator + 1)..], out var id))
            {
                return false;
            }

            cursor = new PageCursor(decoded[..separator], id);

            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when the sort key parses as the type this list orders by.</summary>
    public bool IsDateTime()
        => long.TryParse(SortKey, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    public bool IsInt()
        => int.TryParse(SortKey, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
}
