using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Resolves the authenticated caller to the Registrar they are, and answers
/// what that registrar is allowed to act on.
///
/// This is what closes the impersonation hole: before tokens, the acting
/// registrar was whatever id the request body claimed, so any caller could
/// file a birth as anyone, at any facility. It now comes from the token's
/// subject and nothing else.
/// </summary>
public class CurrentRegistrarService(
    NcbrsDbContext db,
    IHttpContextAccessor httpContextAccessor,
    CountyLookup counties)
{
    private Registrar? _cached;

    /// <summary>
    /// The registrar behind the current token, or null when the subject has
    /// no registrar record -- an authenticated account that was never
    /// provisioned in the registry, which must not be allowed to write.
    /// </summary>
    public async ValueTask<Registrar?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var subject = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? httpContextAccessor.HttpContext?.User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        _cached = await db.Registrars
            .FirstOrDefaultAsync(registrar => registrar.ExternalSubjectId == subject, cancellationToken);

        return _cached;
    }

    /// <summary>
    /// Whether the caller may act on behalf of the given facility.
    ///
    /// A registrar or health worker is confined to their own facility. A
    /// ministry admin acts nationally. A district officer acts across the
    /// facilities of **their own county, and no further** — the same boundary
    /// <see cref="CountyScopeResolver"/> already applies to what they may read.
    ///
    /// That symmetry is the point. Before this, oversight roles were national
    /// for writes while county-scoped for reads, so a district officer could
    /// approve an amendment, verify a late registration, issue a certificate or
    /// enrol a device for a record in a county they were not permitted even to
    /// search. The write reach was wider than the read reach, which is the
    /// wrong way round for a register of legal identities.
    ///
    /// **Fails closed.** If either county cannot be resolved, cross-facility
    /// action is refused: treating "unknown" as matching "unknown" would grant
    /// reach across every facility the hierarchy cannot place — exactly the
    /// facilities least able to have their records double-checked.
    /// </summary>
    public async ValueTask<bool> CanActForFacilityAsync(
        Registrar registrar,
        Guid facilityId,
        CancellationToken cancellationToken = default)
    {
        if (registrar.FacilityId == facilityId)
        {
            return true;
        }

        var user = httpContextAccessor.HttpContext?.User;
        if (user is null)
        {
            return false;
        }

        if (user.IsInRole(NcbrsRoles.MinistryAdmin))
        {
            return true;
        }

        if (!user.IsInRole(NcbrsRoles.DistrictOfficer))
        {
            return false;
        }

        var own = await counties.ForFacilityAsync(registrar.FacilityId, cancellationToken);
        if (string.IsNullOrWhiteSpace(own) || own == AuditLog.Unknown)
        {
            return false;
        }

        var target = await counties.ForFacilityAsync(facilityId, cancellationToken);
        if (string.IsNullOrWhiteSpace(target) || target == AuditLog.Unknown)
        {
            return false;
        }

        return string.Equals(own, target, StringComparison.OrdinalIgnoreCase);
    }
}
