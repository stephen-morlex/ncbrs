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

    /// <summary>Roles that may act beyond a single facility.</summary>
    public static readonly string[] CrossFacility = [DistrictOfficer, MinistryAdmin];
}
