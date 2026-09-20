using NCBRS.Client.Brn;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The device's offline BRN allocation (WS-B5): real numbers from the granted
/// block, a low-block warning while there is still time to ask for more, and a
/// loud provisional fallback when it runs dry — never stopping, never colliding.
/// </summary>
public class DeviceBrnAllocatorTests
{
    [Fact]
    public void HandsOutNumbersFromTheBlockInOrder()
    {
        var allocator = new DeviceBrnAllocator("TABLET-1", blockStart: 100_000, blockEnd: 100_009);

        Assert.Equal(new BrnAllocation("100000", false), allocator.Allocate());
        Assert.Equal(new BrnAllocation("100001", false), allocator.Allocate());
        Assert.Equal(100_002, allocator.NextAvailable);
        Assert.Equal(8, allocator.Remaining);
    }

    [Fact]
    public void ResumesFromPersistedNextAvailable()
    {
        // A device restarts mid-block: it must not re-hand a number it already
        // printed on a slip.
        var allocator = new DeviceBrnAllocator("TABLET-1", 100_000, 100_009, nextAvailable: 100_005);

        Assert.Equal("100005", allocator.Allocate().Value);
        Assert.Equal(4, allocator.Remaining);
    }

    [Fact]
    public void WarnsWhileLowButNotWhenExhausted()
    {
        var allocator = new DeviceBrnAllocator("TABLET-1", 1, 3);

        Assert.False(allocator.IsLow(warnAtOrBelow: 2)); // 3 remaining
        allocator.Allocate();                            // 2 remaining
        Assert.True(allocator.IsLow(2));
        allocator.Allocate();
        allocator.Allocate();                            // exhausted
        Assert.False(allocator.IsLow(2));                // empty is not "low"
        Assert.True(allocator.IsExhausted);
    }

    [Fact]
    public void IssuesProvisionalIdentifiersOnceTheBlockIsExhausted()
    {
        var allocator = new DeviceBrnAllocator("TABLET-7", blockStart: 200, blockEnd: 200);

        var real = allocator.Allocate();
        Assert.False(real.IsProvisional);
        Assert.Equal("200", real.Value);

        var first = allocator.Allocate();
        Assert.True(first.IsProvisional);
        Assert.Equal("PROV-TABLET-7-1", first.Value);
        Assert.True(ProvisionalIdentifier.IsWellFormed(first.Value));

        var second = allocator.Allocate();
        Assert.Equal("PROV-TABLET-7-2", second.Value);

        // The block never advances past its end, however many provisionals issue.
        Assert.Equal(201, allocator.NextAvailable);
        Assert.Equal(2, allocator.ProvisionalSequence);
    }

    [Fact]
    public void RejectsAnImpossibleBlockOrOutOfRangeCursor()
    {
        Assert.Throws<ArgumentException>(() => new DeviceBrnAllocator("D", 100, 50));
        Assert.Throws<ArgumentException>(() => new DeviceBrnAllocator("", 1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceBrnAllocator("D", 1, 10, nextAvailable: 12));
    }
}
