using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// A block of numbers granted to a facility: running numbers
/// <see cref="Start"/> to <see cref="End"/>. For a facility with an office
/// code they are composed (<see cref="BrnFormat"/>) under its code and the
/// year they were issued; without one they are the legacy numeric BRNs
/// themselves.
/// </summary>
public sealed record BrnGrant(long Start, long End, string? OfficeCode, int? Year, int Skipped)
{
    public bool IsComposed => OfficeCode is not null;

    public string BrnAt(long running) => OfficeCode is null
        ? running.ToString(CultureInfo.InvariantCulture)
        : BrnFormat.Compose(OfficeCode, Year!.Value, running);

    public string FirstBrn => BrnAt(Start);

    public string LastBrn => BrnAt(End);
}

/// <summary>
/// Hands out registration numbers from the counter that guards them, and is
/// the only thing that does: a device block, a provisional record being given
/// its real number, and the development seed all draw here, so a number can
/// never be handed out twice by two paths that each kept their own count.
///
/// A facility with an office code draws composed numbers from this year's
/// <see cref="FacilityBrnSequence"/>; one without draws from its legacy
/// numeric range, exactly as before. **Staged, not saved:** the caller saves,
/// and retries on <see cref="DbUpdateException"/> — both counters are
/// concurrency-checked, and a year's counter created by two grants at once
/// collides on its key, so either race surfaces as a failed save rather than
/// two grants of the same numbers.
/// </summary>
public sealed class BrnIssuer(NcbrsDbContext db)
{
    /// <summary>
    /// How many windows of candidate numbers a grant will look past before it
    /// gives up. Each pass skips at least one used number, so a facility whose
    /// counter is behind by less than this catches up in one request.
    /// </summary>
    private const int MaxUsedNumberProbes = 8;

    /// <summary>
    /// Stages a grant of up to <paramref name="size"/> numbers, or null when
    /// the facility has none left to give (its legacy range, or this year's
    /// running numbers, exhausted).
    /// </summary>
    public async Task<BrnGrant?> StageAsync(Facility facility, int size, CancellationToken cancellationToken = default)
    {
        if (facility.OfficeCode is not { } office)
        {
            return await StageAsync(
                () => facility.BrnBlockNextAvailable,
                next => facility.BrnBlockNextAvailable = next,
                facility.BrnBlockEnd, size, office: null, year: null, cancellationToken);
        }

        // The year the number is issued, not the year of the birth: a late
        // registration is numbered when the registry numbers it.
        var year = DateTime.UtcNow.Year;
        var sequence = await db.FacilityBrnSequences.FindAsync([facility.FacilityId, year], cancellationToken);
        if (sequence is null)
        {
            sequence = new FacilityBrnSequence { FacilityId = facility.FacilityId, Year = year };
            db.FacilityBrnSequences.Add(sequence);
        }

        return await StageAsync(
            () => sequence.NextAvailable,
            next => sequence.NextAvailable = next,
            BrnFormat.MaxRunning, size, office, year, cancellationToken);
    }

    /// <summary>The first running number not yet granted this year, for a facility with an office code.</summary>
    public async Task<long> NextRunningThisYearAsync(Facility facility, CancellationToken cancellationToken = default)
    {
        var year = DateTime.UtcNow.Year;
        var sequence = await db.FacilityBrnSequences.AsNoTracking()
            .FirstOrDefaultAsync(s => s.FacilityId == facility.FacilityId && s.Year == year, cancellationToken);
        return sequence?.NextAvailable ?? 1;
    }

    private async Task<BrnGrant?> StageAsync(
        Func<long> next,
        Action<long> advanceTo,
        long ceiling,
        int size,
        string? office,
        int? year,
        CancellationToken cancellationToken)
    {
        // Numbers already on a record are not available to grant, whatever the
        // counter says. The counter tracks what has been *handed out*, not
        // what has been *used*, and those diverge: a record can enter carrying
        // a number from this facility's range without a grant ever happening
        // -- a sync from a device provisioned elsewhere, a restored dump, a
        // seeded environment. For a device that would mean a fortnight of
        // births registered offline against numbers every one of them will be
        // refused on: the collision decision #2 exists to prevent.
        //
        // **Checked at grant time rather than maintained on write,
        // deliberately.** Advancing the counter when a record arrives with a
        // number above it would let a device's own number move a facility's
        // counter, and one device with a bad clock could burn a range with a
        // single high value. The centre never trusts a device-supplied number.
        var skipped = 0;

        for (var probe = 0; probe < MaxUsedNumberProbes; probe++)
        {
            var start = next();
            if (start > ceiling)
            {
                break;
            }

            var end = Math.Min(start + size - 1, ceiling);
            var window = new BrnGrant(start, end, office, year, 0);
            var candidates = new List<string>();
            for (var running = start; running <= end; running++)
            {
                candidates.Add(window.BrnAt(running));
            }

            var taken = await db.BirthRecords
                .Where(record => candidates.Contains(record.Brn))
                .Select(record => record.Brn)
                .ToListAsync(cancellationToken);

            if (taken.Count == 0)
            {
                break;
            }

            skipped += taken.Count;

            // Past the highest one found, not merely past the first: the gap
            // between is free, but re-probing it costs a round trip to
            // rediscover numbers this pass already knows about.
            advanceTo(taken.Select(brn => candidates.IndexOf(brn) + start).Max() + 1);
        }

        var first = next();
        if (first > ceiling)
        {
            return null;
        }

        // Clamped to the ceiling rather than refused when only part of a
        // block remains.
        var last = Math.Min(first + size - 1, ceiling);
        advanceTo(last + 1);

        return new BrnGrant(first, last, office, year, skipped);
    }
}
