using System.Security.Claims;

namespace NCBRS.Services;

/// <summary>
/// Realm role names as they appear in Keycloak, the permissions they carry,
/// and the registry's own RegistrarRole. Kept in one place so a rename in the
/// realm can't silently disagree with the enum, and so who may do what is read
/// from one table (<see cref="Permissions"/>) rather than assembled policy by
/// policy (plan §17 item 29).
/// </summary>
public static class NcbrsRoles
{
    public const string FacilityRegistrar = "facility-registrar";
    public const string CommunityHealthWorker = "community-health-worker";
    public const string DistrictOfficer = "district-officer";

    /// <summary>
    /// The Ministry's records role (decision D2): national approvals,
    /// annulment, the audit trail and reporting. One of the two halves of the
    /// retired <c>ministry-admin</c>, which held every power at once.
    /// </summary>
    public const string RegistrarGeneral = "registrar-general";

    /// <summary>
    /// The Ministry's systems role (decision D2): facilities, administrative
    /// areas, registrars and devices, nationally. <b>It sees no birth record</b>:
    /// running the system and reading the register are separate duties, and an
    /// account that sets up facilities has no reason to read who was born in
    /// them.
    /// </summary>
    public const string SystemAdmin = "system-admin";

    /// <summary>Anyone permitted to register a birth.</summary>
    public const string CanRegisterBirths = nameof(CanRegisterBirths);

    /// <summary>
    /// Issuing and reprinting a birth certificate (decision D1). Not community
    /// health workers: they record births, and the legal document is issued by
    /// a registrar. Recording that a printed certificate reached the family
    /// stays with everyone who registers births, since at a post that is often
    /// the community health worker handing it over.
    /// </summary>
    public const string CanIssueCertificates = nameof(CanIssueCertificates);

    /// <summary>
    /// Reading birth records: the lookup by number, search and a record's
    /// history. Everyone who works with records; not the system admin.
    /// </summary>
    public const string CanReadRecords = nameof(CanReadRecords);

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
    /// level above the other review roles, with the registrar-general.
    /// </summary>
    public const string CanAnnulRegistrations = nameof(CanAnnulRegistrations);

    /// <summary>
    /// Enrolling a device decides which hardware may write to the register
    /// at all, so it sits with the district officers who issue and collect
    /// the tablets -- not with the facility staff using them -- and with the
    /// system admin nationally.
    /// </summary>
    public const string CanEnrolDevices = nameof(CanEnrolDevices);

    /// <summary>
    /// Reading the audit trail.
    ///
    /// Its own capability rather than borrowed from another oversight policy,
    /// because it is a different kind of access: the trail records what every
    /// other endpoint did, and since W1 it also records the names people
    /// searched for. Someone trawling it to see what colleagues looked up is
    /// precisely the misuse it exists to expose.
    /// </summary>
    public const string CanReadAuditTrail = nameof(CanReadAuditTrail);

    /// <summary>
    /// Bringing a facility (or an administrative area) into the registry,
    /// which allocates it a range of registration numbers. The system admin's:
    /// ranges are national, and two facilities given overlapping ones would
    /// issue the same number to two children.
    /// </summary>
    public const string CanManageFacilities = nameof(CanManageFacilities);

    /// <summary>
    /// Onboarding and withdrawing registrars. District officers for their own
    /// county, as with every other write, and only facility staff; the system
    /// admin anywhere and any role. A district officer able to create another
    /// district officer could widen their own oversight.
    /// </summary>
    public const string CanManageRegistrars = nameof(CanManageRegistrars);

    /// <summary>The dashboards: a district officer's own county, the registrar-general's country.</summary>
    public const string CanReadReporting = nameof(CanReadReporting);

    /// <summary>The DHIS2 export: national, so the registrar-general's alone.</summary>
    public const string CanExportReporting = nameof(CanExportReporting);

    /// <summary>
    /// Who holds each permission. The one table both services build their
    /// policies from; the web client restates it in <c>roles.ts</c>, held to
    /// it by <c>RolePermissionsTests</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Permissions = new Dictionary<string, string[]>
    {
        [CanRegisterBirths] = [FacilityRegistrar, CommunityHealthWorker, DistrictOfficer, RegistrarGeneral],
        [CanIssueCertificates] = [FacilityRegistrar, DistrictOfficer, RegistrarGeneral],
        [CanReadRecords] = [FacilityRegistrar, CommunityHealthWorker, DistrictOfficer, RegistrarGeneral],
        [CanReviewDuplicates] = [DistrictOfficer, RegistrarGeneral],
        [CanApproveAmendments] = [DistrictOfficer, RegistrarGeneral],
        [CanApproveLateRegistrations] = [DistrictOfficer, RegistrarGeneral],
        [CanAnnulRegistrations] = [RegistrarGeneral],
        [CanReadAuditTrail] = [DistrictOfficer, RegistrarGeneral],
        [CanEnrolDevices] = [DistrictOfficer, SystemAdmin],
        [CanManageRegistrars] = [DistrictOfficer, SystemAdmin],
        [CanManageFacilities] = [SystemAdmin],
        [CanReadReporting] = [DistrictOfficer, RegistrarGeneral],
        [CanExportReporting] = [RegistrarGeneral],
    };

    /// <summary>
    /// The Ministry's roles act nationally: everyone else is held to their own
    /// facility or county. What each may do nationally is still only what its
    /// permissions allow.
    /// </summary>
    public static bool IsNational(ClaimsPrincipal user) => user.IsInRole(RegistrarGeneral) || user.IsInRole(SystemAdmin);

    /// <summary>The realm role a registry role corresponds to. The retired Ministry role corresponds to none.</summary>
    public static string? RealmRoleFor(NCBRS.Models.RegistrarRole role) => role switch
    {
        NCBRS.Models.RegistrarRole.FacilityRegistrar => FacilityRegistrar,
        NCBRS.Models.RegistrarRole.CommunityHealthWorker => CommunityHealthWorker,
        NCBRS.Models.RegistrarRole.DistrictOfficer => DistrictOfficer,
        NCBRS.Models.RegistrarRole.RegistrarGeneral => RegistrarGeneral,
        NCBRS.Models.RegistrarRole.SystemAdmin => SystemAdmin,
        _ => null,
    };
}
