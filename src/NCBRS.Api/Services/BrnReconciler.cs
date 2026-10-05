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
    NotYetAllocated,

    /// <summary>
    /// Shaped like a composed BRN but its check character disagrees: a
    /// character was misread or mistyped, or two were swapped, on the way in.
    /// </summary>
    Mistyped,

    /// <summary>A composed BRN naming an office other than this facility's.</summary>
    WrongOffice
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
    /// <summary>
    /// <paramref name="sequenceForItsYear"/> is the facility's running-number
    /// counter for the year a composed BRN names, or null if it has none --
    /// in which case nothing was ever issued in that year. Legacy numeric
    /// BRNs are checked against the facility's numeric range, as they always
    /// were, whether or not the facility has since been given an office code:
    /// a number already issued is never renumbered.
    /// </summary>
    public static BrnReconciliationResult Reconcile(string brn, Facility facility, FacilityBrnSequence? sequenceForItsYear = null)
    {
        switch (BrnFormat.Read(brn, out var parts))
        {
            case BrnReading.Mistyped:
                return new BrnReconciliationResult(
                    BrnReconciliation.Mistyped,
                    $"BRN '{brn}' has a check character that does not match the rest of the number.");

            case BrnReading.Composed:
                return ReconcileComposed(brn, parts!, facility, sequenceForItsYear);
        }

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

    private static BrnReconciliationResult ReconcileComposed(
        string brn, BrnParts parts, Facility facility, FacilityBrnSequence? sequence)
    {
        // Confirmed only as the registry writes it. A device composes the
        // number with the same code, so anything else was retyped on the way.
        if (brn != parts.Composed)
        {
            return new BrnReconciliationResult(
                BrnReconciliation.Unparseable,
                $"BRN '{brn}' is not written as the registry writes it ('{parts.Composed}').");
        }

        if (facility.OfficeCode is null || parts.OfficeCode != facility.OfficeCode)
        {
            return new BrnReconciliationResult(
                BrnReconciliation.WrongOffice,
                $"BRN {brn} names office '{parts.OfficeCode}', but facility '{facility.FacilityId}' is "
                + (facility.OfficeCode is null ? "not yet given an office code." : $"office '{facility.OfficeCode}'."));
        }

        // NextAvailable is the first running number not yet handed to any
        // device that year, so anything at or above it was never granted.
        if (sequence is null || sequence.FacilityId != facility.FacilityId || sequence.Year != parts.Year
            || parts.Running >= sequence.NextAvailable)
        {
            return new BrnReconciliationResult(
                BrnReconciliation.NotYetAllocated,
                $"BRN {brn} has not been allocated to any device for facility '{facility.FacilityId}'; "
                + $"in {parts.Year} the registry has issued running numbers up to {(sequence?.Year == parts.Year ? sequence.NextAvailable - 1 : 0)}.");
        }

        return new BrnReconciliationResult(BrnReconciliation.Confirmed);
    }
}
