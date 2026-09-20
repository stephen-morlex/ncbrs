using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// The administrative area an act is accountable to — the <b>county</b>,
/// resolved by walking up a facility's <see cref="Facility.AdministrativeArea"/>
/// to its county ancestor. It is what audit rows and the reporting projection
/// snapshot: a stable, human-meaningful county code rather than a surrogate id.
///
/// The walk almost always reaches a county. A facility not linked to an area,
/// or sitting at a level with no county ancestor, falls back to its flat
/// <see cref="Facility.CountyCode"/> (the same key SQL scope filters use), so
/// no act goes unstamped.
///
/// Scoped and memoised: a single request writes several audit rows against the
/// same facility — a registration writes three — and resolving once per row
/// would turn one insert into many round trips. The area rows walked are cached
/// too, so a chain is loaded at most once per request.
/// </summary>
public class CountyLookup(NcbrsDbContext db)
{
    private readonly Dictionary<Guid, string> _byFacility = [];
    private readonly Dictionary<string, string> _byBrn = [];
    private readonly Dictionary<Guid, AreaRow?> _areas = [];

    private sealed record AreaRow(Guid? ParentId, AdministrativeLevel Level, string Code);

    public async ValueTask<string> ForFacilityAsync(Guid facilityId, CancellationToken cancellationToken = default)
    {
        if (_byFacility.TryGetValue(facilityId, out var cached))
        {
            return cached;
        }

        var facility = await db.Facilities
            .Where(f => f.FacilityId == facilityId)
            .Select(f => new { f.CountyCode, f.AdministrativeAreaId })
            .FirstOrDefaultAsync(cancellationToken);

        var resolved = facility is null
            ? AuditLog.Unknown
            : await ResolveCountyAsync(facility.AdministrativeAreaId, facility.CountyCode, cancellationToken);

        _byFacility[facilityId] = resolved;
        return resolved;
    }

    /// <summary>
    /// The county of the facility a record was registered at — the area the
    /// act belongs to, not the mother's or child's residence.
    /// </summary>
    public ValueTask<string> ForRecordAsync(BirthRecord record, CancellationToken cancellationToken = default)
        => ForFacilityAsync(record.FacilityId, cancellationToken);

    /// <summary>
    /// The county of the record with this number, resolving a provisional
    /// identifier as readily as a BRN. Not always the actor's county: a
    /// ministry admin annulling a record elsewhere produces a row that belongs
    /// to the county whose register changed.
    /// </summary>
    public async ValueTask<string> ForBrnAsync(string brn, CancellationToken cancellationToken = default)
    {
        if (_byBrn.TryGetValue(brn, out var cached))
        {
            return cached;
        }

        var facility = await db.BirthRecords
            .Where(record => record.Brn == brn || record.ProvisionalIdentifier == brn)
            .Select(record => new { record.Facility!.CountyCode, record.Facility!.AdministrativeAreaId })
            .FirstOrDefaultAsync(cancellationToken);

        var resolved = facility is null
            ? AuditLog.Unknown
            : await ResolveCountyAsync(facility.AdministrativeAreaId, facility.CountyCode, cancellationToken);

        _byBrn[brn] = resolved;
        return resolved;
    }

    /// <summary>The county of the facility a registrar belongs to.</summary>
    public ValueTask<string> ForRegistrarAsync(Registrar registrar, CancellationToken cancellationToken = default)
        => ForFacilityAsync(registrar.FacilityId, cancellationToken);

    /// <summary>
    /// The county code for an area, walking up its parent chain. Falls back to
    /// the facility's flat <see cref="Facility.CountyCode"/> when there is no
    /// area linked or no county ancestor, so a facility is always stamped —
    /// the two are kept consistent, and the flat key is what SQL scope filters
    /// use in any case.
    /// </summary>
    private async ValueTask<string> ResolveCountyAsync(
        Guid? areaId, string? fallbackCounty, CancellationToken cancellationToken)
    {
        var fallback = string.IsNullOrWhiteSpace(fallbackCounty) ? AuditLog.Unknown : fallbackCounty;

        // Bounded walk: the hierarchy is at most a handful deep, and the guard
        // stops a cycle from a malformed tree turning this into a hang.
        var current = areaId;
        for (var step = 0; current is { } id && step < 16; step++)
        {
            var area = await LoadAreaAsync(id, cancellationToken);
            if (area is null)
            {
                break;
            }

            if (area.Level == AdministrativeLevel.County)
            {
                return area.Code;
            }

            current = area.ParentId;
        }

        return fallback;
    }

    private async ValueTask<AreaRow?> LoadAreaAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_areas.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var area = await db.AdministrativeAreas
            .Where(a => a.AdministrativeAreaId == id)
            .Select(a => new AreaRow(a.ParentId, a.Level, a.Code))
            .FirstOrDefaultAsync(cancellationToken);

        _areas[id] = area;
        return area;
    }
}
