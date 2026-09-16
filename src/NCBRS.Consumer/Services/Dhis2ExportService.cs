using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Models;

namespace NCBRS.Consumer.Services;

/// <summary>
/// Builds an anonymised aggregate export for DHIS2 (plan E4).
///
/// This is the one piece of WS-E that needs no data-sharing agreement,
/// because it shares no personal data: the unit of the payload is a
/// district-month, and nothing identifying a birth, a child, a parent or a
/// facility leaves the registry.
///
/// **Aggregation alone does not make data anonymous, and that is the whole
/// design problem here.** A count of one, in a small area, for a rare event,
/// identifies a family — and the rarest events in a birth registry are a
/// stillbirth and a mother who died in childbirth. Someone who already knows
/// of one such birth in their district that month learns the rest of the
/// record from a published figure of "1".
///
/// So three rules apply, and each exists to close a different way of reading
/// a person out of a table:
///
/// 1. **A district with too few births is not exported at all.** Suppressing
///    individual cells while publishing the district would still narrow every
///    figure to a handful of families.
/// 2. **A breakdown is published only if every part of it clears the
///    threshold.** Suppressing one cell of a decomposition whose total is
///    published is no protection: the missing value is the total minus the
///    rest. Either the whole breakdown goes or none of it does.
/// 3. **Rare-event counts below the threshold are simply absent.** Absence
///    then means "somewhere between zero and the threshold", which is a range,
///    not a value. Publishing the zeros and suppressing only the ones would
///    make every gap mean "at least one".
/// </summary>
public class Dhis2ExportService(ReadModelDbContext db, Dhis2ExportOptions options)
{
    /// <summary>
    /// <paramref name="period"/> is DHIS2 monthly form, "YYYYMM".
    /// </summary>
    public async Task<Dhis2Export> ExportAsync(string period, CancellationToken cancellationToken = default)
    {
        var (from, to) = MonthOf(period);

        // Counted by date of occurrence, as vital statistics are (UN P&R
        // Rev. 3) -- a birth belongs to the month it happened in, not the
        // month a village post managed to reach the centre.
        var births = await db.RegistrationFacts
            .Where(fact => fact.AnnulledAtUtc == null
                           && fact.DateOfBirth >= from
                           && fact.DateOfBirth < to)
            .ToListAsync(cancellationToken);

        var brns = births.Select(fact => fact.Brn).ToList();

        var neonatalByBrn = await db.NeonatalOutcomeFacts
            .Where(fact => brns.Contains(fact.Brn))
            .Select(fact => fact.Brn)
            .ToListAsync(cancellationToken);

        var maternalByBrn = await db.MaternalOutcomeFacts
            .Where(fact => brns.Contains(fact.Brn))
            .Select(fact => fact.Brn)
            .ToListAsync(cancellationToken);

        var district = births.ToLookup(fact => fact.DistrictId);

        var values = new List<Dhis2DataValue>();
        var suppressed = new List<Dhis2Suppression>();
        var unmapped = new List<string>();

        foreach (var group in district.OrderBy(entry => entry.Key))
        {
            if (!options.OrgUnits.TryGetValue(group.Key, out var orgUnit))
            {
                // Never guessed. An org unit invented here files a district's
                // births against somebody else's catchment.
                unmapped.Add(group.Key);
                continue;
            }

            var facts = group.ToList();
            var live = facts.Where(fact => fact.VitalEventType != "FetalDeath").ToList();

            // Rule 1: too few births in the district, so nothing about it goes
            // out -- not even the total.
            if (live.Count < options.MinimumCellSize)
            {
                suppressed.Add(new Dhis2Suppression(orgUnit,
                    $"Fewer than {options.MinimumCellSize} live births in the period; "
                    + "the whole district is withheld rather than published as small cells."));

                continue;
            }

            Add(values, options.LiveBirths, period, orgUnit, live.Count);

            AddBreakdown(values, suppressed, period, orgUnit,
                "live births by sex",
                (options.LiveBirthsMale, live.Count(fact => fact.Sex == "Male")),
                (options.LiveBirthsFemale, live.Count(fact => fact.Sex == "Female")));

            // Timeliness is a decomposition of live births too: publishing the
            // count inside the window next to the total gives away the count
            // outside it.
            var withinWindow = live.Count(fact => fact.WithinStatutoryWindow == true);
            var judged = live.Count(fact => fact.WithinStatutoryWindow.HasValue);

            AddBreakdown(values, suppressed, period, orgUnit,
                "registration timeliness",
                (options.RegisteredWithinWindow, withinWindow),
                (DataElement: null, Count: judged - withinWindow));

            // Rule 3: rare events stand alone rather than decomposing a
            // published total, so each is withheld on its own when small.
            AddRare(values, options.FetalDeaths, period, orgUnit,
                facts.Count(fact => fact.VitalEventType == "FetalDeath"));

            AddRare(values, options.NeonatalDeaths, period, orgUnit,
                live.Count(fact => neonatalByBrn.Contains(fact.Brn)));

            AddRare(values, options.MaternalDeaths, period, orgUnit,
                live.Count(fact => maternalByBrn.Contains(fact.Brn)));
        }

        return new Dhis2Export(new Dhis2DataValueSet(period, values), suppressed, unmapped);
    }

    private static void Add(
        List<Dhis2DataValue> values, string? dataElement, string period, string orgUnit, int count)
    {
        // An unconfigured data element is skipped rather than invented: a
        // value posted against a UID DHIS2 does not know is rejected at best,
        // and filed against the wrong indicator at worst.
        if (!string.IsNullOrWhiteSpace(dataElement))
        {
            values.Add(new Dhis2DataValue(
                dataElement, period, orgUnit, count.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Rule 2. A breakdown goes out whole or not at all, because a
    /// decomposition with one cell missing and its total published is not
    /// suppressed — it is arithmetic.
    /// </summary>
    private void AddBreakdown(
        List<Dhis2DataValue> values,
        List<Dhis2Suppression> suppressed,
        string period,
        string orgUnit,
        string description,
        params (string? DataElement, int Count)[] cells)
    {
        if (cells.Any(cell => cell.Count > 0 && cell.Count < options.MinimumCellSize))
        {
            suppressed.Add(new Dhis2Suppression(orgUnit,
                $"The {description} breakdown has a cell below {options.MinimumCellSize}; "
                + "the whole breakdown is withheld, because the missing cell would be "
                + "recoverable from the published total."));

            return;
        }

        foreach (var cell in cells)
        {
            Add(values, cell.DataElement, period, orgUnit, cell.Count);
        }
    }

    /// <summary>
    /// Rule 3. Below the threshold the value is absent — which reads as
    /// "between zero and the threshold" rather than as a number.
    /// </summary>
    private void AddRare(
        List<Dhis2DataValue> values, string? dataElement, string period, string orgUnit, int count)
    {
        if (count >= options.MinimumCellSize)
        {
            Add(values, dataElement, period, orgUnit, count);
        }
    }

    /// <summary>"YYYYMM", the DHIS2 monthly period.</summary>
    private static (DateTime From, DateTime To) MonthOf(string period)
    {
        if (period?.Length != 6
            || !int.TryParse(period[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(period[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || month is < 1 or > 12)
        {
            throw new ArgumentException(
                $"'{period}' is not a DHIS2 monthly period. Expected YYYYMM, for example 202609.",
                nameof(period));
        }

        var from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);

        return (from, from.AddMonths(1));
    }
}
