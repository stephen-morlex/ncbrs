using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Realm role names as they appear in Keycloak, paired with the registry's
/// own RegistrarRole. Kept in one place so a rename in the realm can't
/// silently disagree with the enum.
/// </summary>
public static class NcbrsRoles
{
    public const string FacilityRegistrar = "facility-registrar";
    public const string CommunityHealthWorker = "community-health-worker";
    public const string DistrictOfficer = "district-officer";
    public const string MinistryAdmin = "ministry-admin";

    /// <summary>Anyone permitted to register a birth.</summary>
    public const string CanRegisterBirths = nameof(CanRegisterBirths);

    /// <summary>
    /// Adjudicating a suspected duplicate decides whether a citizen has one
    /// legal identity or two, so it sits with oversight roles rather than
    /// the facility staff who filed the records under review.
    /// </summary>
    public const string CanReviewDuplicates = nameof(CanReviewDuplicates);

    /// <summary>
    /// Approving a correction to a certificate-signed field decides which
    /// person the register describes, so it sits with oversight roles rather
    /// than the facility staff who submitted it. The service additionally
    /// refuses an approval by the submitter, whatever their role.
    /// </summary>
    public const string CanApproveAmendments = nameof(CanApproveAmendments);

    /// <summary>
    /// Verifying a late registration confirms a date of birth that nobody
    /// contemporaneous is left to contradict, so it sits with oversight
    /// roles. The service additionally refuses a verification by the
    /// registrar who filed it.
    /// </summary>
    public const string CanApproveLateRegistrations = nameof(CanApproveLateRegistrations);

    /// <summary>
    /// Annulment withdraws a legal identity outright, rather than correcting
    /// how it reads or choosing between two records for one child. It sits a
    /// level above the other review roles, at the ministry.
    /// </summary>
    public const string CanAnnulRegistrations = nameof(CanAnnulRegistrations);

    /// <summary>
    /// Enrolling a device decides which hardware may write to the register
    /// at all, so it sits with the district officers who issue and collect
    /// the tablets -- not with the facility staff using them. A device able
    /// to enrol itself, or to be enrolled by whoever is holding it, would
    /// close no hole.
    /// </summary>
    public const string CanEnrolDevices = nameof(CanEnrolDevices);

    /// <summary>Roles that may act beyond a single facility.</summary>
    public static readonly string[] CrossFacility = [DistrictOfficer, MinistryAdmin];
}

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
