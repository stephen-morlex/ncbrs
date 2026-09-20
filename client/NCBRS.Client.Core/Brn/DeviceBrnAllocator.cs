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
/// A pure state machine: it holds no store and does no I/O. The caller persists
/// <see cref="NextAvailable"/> and <see cref="ProvisionalSequence"/> to the local
/// encrypted database (B2) after each allocation, so consumption survives a
/// restart — losing them would re-hand numbers already printed on slips.
/// </summary>
public sealed class DeviceBrnAllocator
{
    private readonly string _deviceId;
    private long _nextAvailable;
    private long _provisionalSequence;

    /// <param name="deviceId">Enrolled device id; the provisional-identifier segment that keeps two exhausted posts from colliding.</param>
    /// <param name="blockStart">First number in the granted block (inclusive).</param>
    /// <param name="blockEnd">Last number in the granted block (inclusive).</param>
    /// <param name="nextAvailable">The next number to hand out, restored from the local store. Defaults to <paramref name="blockStart"/> for a freshly granted block.</param>
    /// <param name="provisionalSequence">The provisional counter, restored from the local store.</param>
    public DeviceBrnAllocator(
        string deviceId, long blockStart, long blockEnd, long? nextAvailable = null, long provisionalSequence = 0)
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
        BlockStart = blockStart;
        BlockEnd = blockEnd;
        _nextAvailable = next;
        _provisionalSequence = provisionalSequence;
    }

    public long BlockStart { get; }

    public long BlockEnd { get; }

    /// <summary>The next number the block will hand out; one past <see cref="BlockEnd"/> once exhausted. Persist this.</summary>
    public long NextAvailable => _nextAvailable;

    /// <summary>How many provisional identifiers have been issued. Persist this.</summary>
    public long ProvisionalSequence => _provisionalSequence;

    /// <summary>Real numbers still available in the block. Zero once exhausted.</summary>
    public long Remaining => IsExhausted ? 0 : BlockEnd - _nextAvailable + 1;

    public bool IsExhausted => _nextAvailable > BlockEnd;

    /// <summary>
    /// Whether the block is running low and the device should ask for another
    /// while it still has connectivity (B5's low-block warning). False once
    /// exhausted — then it is not low, it is empty, which <see cref="IsExhausted"/> says.
    /// </summary>
    public bool IsLow(long warnAtOrBelow) => !IsExhausted && Remaining <= warnAtOrBelow;

    /// <summary>
    /// Hand out the next identifier: a real BRN while the block has numbers,
    /// otherwise a provisional identifier. Advances the counter it drew from,
    /// so the caller must persist the new state.
    /// </summary>
    public BrnAllocation Allocate()
    {
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
