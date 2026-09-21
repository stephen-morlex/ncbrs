using System.Globalization;
using NCBRS.Models;

namespace NCBRS.Client.Brn;

/// <summary>One number the device handed out, and whether it is a real BRN or a provisional fallback.</summary>
public sealed record BrnAllocation(string Value, bool IsProvisional);

/// <summary>
/// WS-B5. The device's own view of the BRN block the centre granted it, and
/// the numbers it hands out from it while offline.
///
/// This is decision #2 living on the device: the block was pre-allocated when
/// the device last had connectivity, and the device — not the server —
/// generates each BRN from it, so a post can register for weeks with no network
/// and never collide with a block granted elsewhere. When the block runs dry the
/// device does not stop (that would send families away) and does not count past
/// the end (that would collide): it issues a loud <c>PROV-</c> provisional
/// identifier instead (draft 6.3), which the centre replaces with a real BRN at
/// reconciliation.
///
/// The <c>PROV-</c> fallback is the last resort, not the plan: while the device
/// still has connectivity the low-block warning tells it to fetch the next block
/// (<c>request-brn-block</c>), which it stages here with
/// <see cref="GrantNextBlock"/>. When the current block runs dry the allocator
/// rolls straight over to the staged one, so a device that topped up in time
/// never issues a provisional identifier at all. Blocks are granted in ascending,
/// non-overlapping ranges, so the staged block always lies beyond the current.
///
/// A pure state machine: it holds no store and does no I/O. The caller persists
/// <see cref="NextAvailable"/>, <see cref="ProvisionalSequence"/> and any staged
/// <see cref="PendingBlockStart"/>/<see cref="PendingBlockEnd"/> to the local
/// encrypted database (B2) after each change, so consumption survives a restart —
/// losing them would re-hand numbers already printed on slips.
/// </summary>
public sealed class DeviceBrnAllocator
{
    private readonly string _deviceId;
    private long _blockStart;
    private long _blockEnd;
    private long _nextAvailable;
    private long _provisionalSequence;
    private long? _pendingStart;
    private long? _pendingEnd;

    /// <param name="deviceId">Enrolled device id; the provisional-identifier segment that keeps two exhausted posts from colliding.</param>
    /// <param name="blockStart">First number in the granted block (inclusive).</param>
    /// <param name="blockEnd">Last number in the granted block (inclusive).</param>
    /// <param name="nextAvailable">The next number to hand out, restored from the local store. Defaults to <paramref name="blockStart"/> for a freshly granted block.</param>
    /// <param name="provisionalSequence">The provisional counter, restored from the local store.</param>
    /// <param name="pendingBlockStart">A next block staged before a restart, restored from the local store; pass with <paramref name="pendingBlockEnd"/> or neither.</param>
    /// <param name="pendingBlockEnd">End of the staged next block.</param>
    public DeviceBrnAllocator(
        string deviceId, long blockStart, long blockEnd, long? nextAvailable = null, long provisionalSequence = 0,
        long? pendingBlockStart = null, long? pendingBlockEnd = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        }

        if (blockEnd < blockStart)
        {
            throw new ArgumentException($"Block end {blockEnd} is before block start {blockStart}.", nameof(blockEnd));
        }

        var next = nextAvailable ?? blockStart;

        // next may sit one past the end — that is an exhausted block, not an error.
        if (next < blockStart || next > blockEnd + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAvailable), next, $"Next-available must be within [{blockStart}, {blockEnd + 1}].");
        }

        if (provisionalSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(provisionalSequence), provisionalSequence, "Cannot be negative.");
        }

        _deviceId = deviceId;
        _blockStart = blockStart;
        _blockEnd = blockEnd;
        _nextAvailable = next;
        _provisionalSequence = provisionalSequence;

        if (pendingBlockStart.HasValue != pendingBlockEnd.HasValue)
        {
            throw new ArgumentException("A staged next block needs both a start and an end, or neither.", nameof(pendingBlockStart));
        }

        if (pendingBlockStart.HasValue)
        {
            StageNextBlock(pendingBlockStart.Value, pendingBlockEnd!.Value);
        }
    }

    public long BlockStart => _blockStart;

    public long BlockEnd => _blockEnd;

    /// <summary>The next number the block will hand out; one past <see cref="BlockEnd"/> once exhausted. Persist this.</summary>
    public long NextAvailable => _nextAvailable;

    /// <summary>How many provisional identifiers have been issued. Persist this.</summary>
    public long ProvisionalSequence => _provisionalSequence;

    /// <summary>First number of the next block staged for roll-over, or null if none is staged. Persist this.</summary>
    public long? PendingBlockStart => _pendingStart;

    /// <summary>Last number of the staged next block, or null. Persist this.</summary>
    public long? PendingBlockEnd => _pendingEnd;

    /// <summary>Whether a next block is already in hand to roll over to.</summary>
    public bool HasPendingBlock => _pendingStart.HasValue;

    /// <summary>Real numbers still available in the block. Zero once exhausted.</summary>
    public long Remaining => IsExhausted ? 0 : _blockEnd - _nextAvailable + 1;

    public bool IsExhausted => _nextAvailable > _blockEnd;

    /// <summary>
    /// Whether the block is running low and the device should ask for another
    /// while it still has connectivity (B5's low-block warning). False once
    /// exhausted — then it is not low, it is empty, which <see cref="IsExhausted"/>
    /// says — and false once a next block is already staged, since the top-up the
    /// warning asks for is already in hand.
    /// </summary>
    public bool IsLow(long warnAtOrBelow) => !HasPendingBlock && !IsExhausted && Remaining <= warnAtOrBelow;

    /// <summary>
    /// Stage the next block the centre granted (via <c>request-brn-block</c>), to
    /// roll over to when the current one runs dry. Requested while online in
    /// response to <see cref="IsLow"/>, so a device that tops up in time never has
    /// to issue a provisional identifier. Refused if a block is already staged, or
    /// if the new range overlaps the current one — blocks are granted ascending
    /// and non-overlapping.
    /// </summary>
    public void GrantNextBlock(long blockStart, long blockEnd) => StageNextBlock(blockStart, blockEnd);

    private void StageNextBlock(long blockStart, long blockEnd)
    {
        if (blockEnd < blockStart)
        {
            throw new ArgumentException($"Block end {blockEnd} is before block start {blockStart}.", nameof(blockEnd));
        }

        // A staged block must lie wholly beyond the current one; anything at or
        // below its end could re-hand a number this block already covers.
        if (blockStart <= _blockEnd)
        {
            throw new ArgumentException(
                $"Next block start {blockStart} overlaps the current block ending at {_blockEnd}.", nameof(blockStart));
        }

        if (_pendingStart.HasValue)
        {
            throw new InvalidOperationException("A next block is already staged; roll over to it before staging another.");
        }

        _pendingStart = blockStart;
        _pendingEnd = blockEnd;
    }

    /// <summary>
    /// Hand out the next identifier: a real BRN while the block has numbers,
    /// otherwise the staged next block if one is in hand, otherwise a provisional
    /// identifier. Advances the counter it drew from (and rolls over a staged
    /// block), so the caller must persist the new state.
    /// </summary>
    public BrnAllocation Allocate()
    {
        if (IsExhausted && _pendingStart.HasValue)
        {
            // The block ran dry but the top-up requested at the low-block warning
            // arrived in time: adopt it and carry on issuing real numbers.
            _blockStart = _pendingStart.Value;
            _blockEnd = _pendingEnd!.Value;
            _nextAvailable = _blockStart;
            _pendingStart = null;
            _pendingEnd = null;
        }

        if (!IsExhausted)
        {
            var brn = _nextAvailable.ToString(CultureInfo.InvariantCulture);
            _nextAvailable++;
            return new BrnAllocation(brn, IsProvisional: false);
        }

        _provisionalSequence++;
        return new BrnAllocation(ProvisionalIdentifier.For(_deviceId, _provisionalSequence), IsProvisional: true);
    }
}
