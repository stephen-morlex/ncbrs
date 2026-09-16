using System.Globalization;
using NCBRS.Models;

namespace NCBRS.Services;

public enum BrnReconciliation
{
    /// <summary>Drawn from a block the registry actually granted this facility.</summary>
    Confirmed,

    /// <summary>Not the numeric identifier a BRN block issues.</summary>
    Unparseable,

    /// <summary>Outside the range this facility was ever assigned.</summary>
    OutsideFacilityRange,

    /// <summary>
    /// Inside the facility's range but at or beyond the point the registry
    /// has handed out. Nobody was given this number.
    /// </summary>
    NotYetAllocated
}

public record BrnReconciliationResult(BrnReconciliation Outcome, string? Detail = null)
{
    public bool Confirmed => Outcome is BrnReconciliation.Confirmed;
}

/// <summary>
/// Checks that a submitted BRN really came from a block the registry granted
/// (draft 5.1: the centre "validates and confirms the BRN as permanent").
///
/// This is what makes the offline block-allocation design an actual
/// guarantee rather than an honour system. Devices generate their own BRNs,
/// which is the whole point -- a village post offline for weeks cannot ask
/// for one -- but nothing about that requires the centre to accept whatever
/// number arrives. Without this check a device could invent identifiers that
/// collide with a block granted to a different facility next month, and the
/// collision would surface years later as two citizens holding the same
/// registration number.
///
/// A record that fails is never refused. The birth happened, and the child
/// has the BRN already printed on a provisional certificate in the family's
/// hands; withdrawing it would create exactly the renumbering the design
/// exists to avoid. It stays provisional and unconfirmed, which is visible
/// and reviewable.
/// </summary>
public static class BrnReconciler
{
    public static BrnReconciliationResult Reconcile(string brn, Facility facility)
    {
        if (!long.TryParse(brn, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return new BrnReconciliationResult(
                BrnReconciliation.Unparseable,
                $"BRN '{brn}' is not a numeric identifier and cannot be matched against "
                + $"facility '{facility.FacilityId}'s allocated blocks.");
        }

        if (number < facility.BrnBlockStart || number > facility.BrnBlockEnd)
        {
            return new BrnReconciliationResult(
                BrnReconciliation.OutsideFacilityRange,
                $"BRN {number} falls outside the range assigned to facility "
                + $"'{facility.FacilityId}' ({facility.BrnBlockStart}-{facility.BrnBlockEnd}).");
        }

        // BrnBlockNextAvailable is the first number not yet handed to any
        // device, so anything at or above it was never granted.
        if (number >= facility.BrnBlockNextAvailable)
        {
            return new BrnReconciliationResult(
                BrnReconciliation.NotYetAllocated,
                $"BRN {number} has not been allocated to any device for facility "
                + $"'{facility.FacilityId}'; the registry has issued up to "
                + $"{facility.BrnBlockNextAvailable - 1}.");
        }

        return new BrnReconciliationResult(BrnReconciliation.Confirmed);
    }
}
