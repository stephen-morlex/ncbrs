using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// The district a facility belongs to, for stamping on audit rows.
///
/// Scoped and memoised, because a single request can write several audit
/// entries against the same facility — a registration writes three — and
/// asking the database once per row would turn one insert into four
/// round trips.
///
/// Returns <see cref="AuditLog.Unknown"/> rather than throwing when a
/// facility cannot be resolved. An audit row must never be the reason a
/// registration fails: the act being recorded already happened, and losing
/// the record of it to protect a column would be the wrong trade in exactly
/// the place this table exists to hold.
/// </summary>
public class DistrictLookup(NcbrsDbContext db)
{
    private readonly Dictionary<Guid, string> _byFacility = [];
    private readonly Dictionary<string, string> _byBrn = [];

    public async ValueTask<string> ForFacilityAsync(
        Guid facilityId,
        CancellationToken cancellationToken = default)
    {
        if (_byFacility.TryGetValue(facilityId, out var cached))
        {
            return cached;
        }

        var district = await db.Facilities
            .Where(facility => facility.FacilityId == facilityId)
            .Select(facility => facility.DistrictId)
            .FirstOrDefaultAsync(cancellationToken);

        var resolved = string.IsNullOrWhiteSpace(district) ? AuditLog.Unknown : district;

        _byFacility[facilityId] = resolved;

        return resolved;
    }

    /// <summary>
    /// The district of the facility a record was registered at.
    ///
    /// Note this is the facility's district, not the mother's or the child's
    /// residence. The trail records where the *act* took place, which is what
    /// a district is being asked to account for.
    /// </summary>
    public ValueTask<string> ForRecordAsync(
        BirthRecord record,
        CancellationToken cancellationToken = default)
        => record.Facility is { } facility
            ? ValueTask.FromResult(facility.DistrictId)
            : ForFacilityAsync(record.FacilityId, cancellationToken);

    /// <summary>
    /// The district of the record with this number, resolving a provisional
    /// identifier as readily as a BRN — a device's slip names the same birth.
    ///
    /// The commonest shape by far: almost every audited act names a record,
    /// and the record decides which district must account for it. Note that
    /// is **not** always the actor's district: a ministry admin annulling a
    /// record in another district produces a row that belongs to the district
    /// whose register changed, not to the Ministry.
    /// </summary>
    public async ValueTask<string> ForBrnAsync(
        string brn,
        CancellationToken cancellationToken = default)
    {
        if (_byBrn.TryGetValue(brn, out var cached))
        {
            return cached;
        }

        var district = await db.BirthRecords
            .Where(record => record.Brn == brn || record.ProvisionalIdentifier == brn)
            .Select(record => record.Facility!.DistrictId)
            .FirstOrDefaultAsync(cancellationToken);

        var resolved = string.IsNullOrWhiteSpace(district) ? AuditLog.Unknown : district;

        _byBrn[brn] = resolved;

        return resolved;
    }

    /// <summary>The district of the facility a registrar belongs to.</summary>
    public ValueTask<string> ForRegistrarAsync(
        Registrar registrar,
        CancellationToken cancellationToken = default)
        => registrar.Facility is { } facility
            ? ValueTask.FromResult(facility.DistrictId)
            : ForFacilityAsync(registrar.FacilityId, cancellationToken);
}
