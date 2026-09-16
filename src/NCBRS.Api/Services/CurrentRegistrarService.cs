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
public class CurrentRegistrarService(NcbrsDbContext db, IHttpContextAccessor httpContextAccessor)
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
    /// Whether the caller may act on behalf of the given facility. A
    /// registrar or health worker is confined to their own; district officers
    /// and ministry admins oversee several by definition, so they are not.
    /// </summary>
    public bool CanActForFacility(Registrar registrar, Guid facilityId)
    {
        if (registrar.FacilityId == facilityId)
        {
            return true;
        }

        var user = httpContextAccessor.HttpContext?.User;

        return user is not null && NcbrsRoles.CrossFacility.Any(user.IsInRole);
    }
}
