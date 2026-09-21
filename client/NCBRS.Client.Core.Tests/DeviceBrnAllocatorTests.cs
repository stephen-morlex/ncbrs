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

    [Fact]
    public void RollsOverToAStagedBlockInsteadOfIssuingProvisionals()
    {
        // Topped up at the low-block warning, before running dry.
        var allocator = new DeviceBrnAllocator("TABLET-1", 1, 2);
        allocator.GrantNextBlock(100, 101);

        Assert.Equal("1", allocator.Allocate().Value);
        Assert.Equal("2", allocator.Allocate().Value); // current block now exhausted

        var rolled = allocator.Allocate();              // rolls straight over
        Assert.False(rolled.IsProvisional);
        Assert.Equal("100", rolled.Value);
        Assert.Equal("101", allocator.Allocate().Value);
        Assert.Equal(0, allocator.ProvisionalSequence); // never fell back
        Assert.False(allocator.HasPendingBlock);        // the staged block was consumed
    }

    [Fact]
    public void SuppressesTheLowWarningOnceANextBlockIsStaged()
    {
        var allocator = new DeviceBrnAllocator("TABLET-1", 1, 3);
        allocator.Allocate();                           // 2 remaining
        Assert.True(allocator.IsLow(warnAtOrBelow: 2));

        allocator.GrantNextBlock(10, 20);
        Assert.False(allocator.IsLow(2));               // top-up is in hand; stop asking
    }

    [Fact]
    public void FallsBackToProvisionalOnlyWhenNothingIsStaged()
    {
        // No top-up arrived (offline the whole time): the fallback still fires,
        // and a block granted afterwards rolls the device back onto real numbers.
        var allocator = new DeviceBrnAllocator("TABLET-9", 5, 5);
        Assert.Equal("5", allocator.Allocate().Value);
        Assert.True(allocator.Allocate().IsProvisional); // PROV-TABLET-9-1

        allocator.GrantNextBlock(50, 51);
        var rolled = allocator.Allocate();
        Assert.False(rolled.IsProvisional);
        Assert.Equal("50", rolled.Value);
        Assert.Equal(1, allocator.ProvisionalSequence);  // the earlier PROV slip is not forgotten
    }

    [Fact]
    public void RestoresAStagedBlockAcrossARestart()
    {
        var allocator = new DeviceBrnAllocator(
            "TABLET-1", 1, 2, nextAvailable: 3, pendingBlockStart: 100, pendingBlockEnd: 101);

        Assert.True(allocator.IsExhausted);
        Assert.True(allocator.HasPendingBlock);
        Assert.Equal("100", allocator.Allocate().Value); // rolls over to the persisted staged block
    }

    [Fact]
    public void RejectsAnOverlappingOrDuplicateStagedBlock()
    {
        var allocator = new DeviceBrnAllocator("TABLET-1", 100, 200);

        Assert.Throws<ArgumentException>(() => allocator.GrantNextBlock(150, 300)); // overlaps current
        Assert.Throws<ArgumentException>(() => allocator.GrantNextBlock(50, 60));   // below current
        Assert.Throws<ArgumentException>(() => allocator.GrantNextBlock(300, 250)); // end before start

        allocator.GrantNextBlock(300, 400);
        Assert.Throws<InvalidOperationException>(() => allocator.GrantNextBlock(500, 600)); // already staged
    }
}
