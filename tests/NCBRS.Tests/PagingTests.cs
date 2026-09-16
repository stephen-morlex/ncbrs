using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The paging contract (plan W7).
///
/// The reason it is keyset rather than offset is a review queue: working an
/// item is what removes it, so an offset counted from the start steps past
/// the items that shuffled down into the gap. On these queues a skipped item
/// is a birth record correction nobody looks at, by a reviewer who believes
/// they have worked the whole queue.
/// </summary>
public class PagingTests
{
    // --- limits ----------------------------------------------------------------

    [Fact]
    public void AnAbsentLimitUsesTheDefault()
        => Assert.Equal(PageRequest.DefaultLimit, new PageRequest().EffectiveLimit);

    /// <summary>
    /// Clamped, not rejected. A client asking for ten thousand rows has made a
    /// judgement about its own needs; it is not an error worth failing a
    /// district officer's queue over. It simply does not get ten thousand rows.
    /// </summary>
    [Theory]
    [InlineData(10_000, PageRequest.MaxLimit)]
    [InlineData(PageRequest.MaxLimit + 1, PageRequest.MaxLimit)]
    [InlineData(0, PageRequest.DefaultLimit)]
    [InlineData(-5, PageRequest.DefaultLimit)]
    [InlineData(25, 25)]
    public void AnOutOfRangeLimitIsClamped(int requested, int expected)
        => Assert.Equal(expected, new PageRequest { Limit = requested }.EffectiveLimit);

    // --- cursors ----------------------------------------------------------------

    [Fact]
    public void ATimeCursorRoundTrips()
    {
        var at = new DateTime(2026, 9, 16, 8, 30, 0, DateTimeKind.Utc);
        var id = Guid.CreateVersion7();

        Assert.True(PageCursor.TryDecode(PageCursor.For(at, id).Encode(), out var decoded));
        Assert.True(decoded.IsDateTime());
        Assert.Equal(at, decoded.AsDateTime());
        Assert.Equal(id, decoded.Id);
    }

    /// <summary>
    /// The duplicate queue orders by match score, not time — so one cursor has
    /// to carry either.
    /// </summary>
    [Fact]
    public void ARankCursorRoundTrips()
    {
        var id = Guid.CreateVersion7();

        Assert.True(PageCursor.TryDecode(PageCursor.For(87, id).Encode(), out var decoded));
        Assert.True(decoded.IsInt());
        Assert.Equal(87, decoded.AsInt());
        Assert.Equal(id, decoded.Id);
    }

    /// <summary>
    /// A cursor that cannot be read is refused rather than ignored. Silently
    /// falling back to the first page would show a reviewer the top of the
    /// queue while they believed they were three pages in — which is the same
    /// skipping failure by a different route.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!!")]
    [InlineData("bm90LWEtY3Vyc29y")]          // valid base64, wrong shape
    public void AnUnreadableCursorIsRefused(string? encoded)
        => Assert.False(PageCursor.TryDecode(encoded, out _));

    [Fact]
    public void ACursorWhoseIdIsNotAGuidIsRefused()
    {
        var forged = Convert.ToBase64String("12345:not-a-guid"u8.ToArray());

        Assert.False(PageCursor.TryDecode(forged, out _));
    }

    // --- pages -------------------------------------------------------------------

    /// <summary>
    /// The probe row is how a next page is detected without a second query. It
    /// must not be returned to the caller, or every page would show one row
    /// belonging to the next.
    /// </summary>
    [Fact]
    public void AFullPageTrimsTheProbeRowAndOffersACursor()
    {
        var rows = Enumerable.Range(1, 4).ToList();

        var page = Page<int>.From(rows, total: 40, limit: 3, last => PageCursor.For(last, Guid.Empty));

        Assert.Equal([1, 2, 3], page.Items);
        Assert.Equal(40, page.Total);
        Assert.NotNull(page.NextCursor);
    }

    /// <summary>
    /// The cursor comes from the last row returned, never the probe. A cursor
    /// pointing at a row the caller has not seen would skip it.
    /// </summary>
    [Fact]
    public void TheCursorPointsAtTheLastRowTheCallerActuallySaw()
    {
        var rows = Enumerable.Range(1, 4).ToList();

        var page = Page<int>.From(rows, total: 40, limit: 3, last => PageCursor.For(last, Guid.Empty));

        Assert.True(PageCursor.TryDecode(page.NextCursor, out var cursor));
        Assert.Equal(3, cursor.AsInt());
    }

    [Fact]
    public void ALastPageOffersNoCursor()
    {
        var page = Page<int>.From([1, 2], total: 2, limit: 3, last => PageCursor.For(last, Guid.Empty));

        Assert.Equal([1, 2], page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void AnEmptyPageIsNotAnError()
    {
        var page = Page<int>.From([], total: 0, limit: 10, last => PageCursor.For(last, Guid.Empty));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Null(page.NextCursor);
    }

    /// <summary>
    /// Total counts the whole backlog, not the page — it is what tells a
    /// district officer how much work is waiting.
    /// </summary>
    [Fact]
    public void TotalDescribesTheBacklogRatherThanThePage()
    {
        var page = Page<int>.From([1, 2, 3, 4], total: 900, limit: 3, last => PageCursor.For(last, Guid.Empty));

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(900, page.Total);
    }
}
