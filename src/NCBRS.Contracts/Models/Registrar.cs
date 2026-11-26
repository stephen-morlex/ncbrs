namespace NCBRS.Models;

public enum RegistrarRole
{
    FacilityRegistrar,
    CommunityHealthWorker,
    DistrictOfficer,

    /// <summary>
    /// Retired by the role split (plan §17 item 29, decision D2): the Ministry
    /// is now <see cref="RegistrarGeneral"/> or <see cref="SystemAdmin"/>. Kept
    /// so rows bound before the split still load; nothing is bound to it now,
    /// and the realm role it named grants nothing.
    /// </summary>
    MinistryAdmin,

    /// <summary>The Ministry's records role: national approvals, annulment, the audit trail and reporting.</summary>
    RegistrarGeneral,

    /// <summary>The Ministry's systems role: facilities, areas, registrars and devices. Sees no birth record.</summary>
    SystemAdmin,
}

public class Registrar
{
    public Guid RegistrarId { get; set; } = Guid.CreateVersion7();

    public Guid FacilityId { get; set; }
    public Facility? Facility { get; set; }

    /// <summary>
    /// The identity provider's subject claim for this person (Keycloak's
    /// "sub"). Held as a separate column rather than reused as the primary
    /// key so the registry's own keys stay independent of the IdP: Keycloak
    /// can be replaced, or a user account re-created, without orphaning the
    /// birth records this registrar signed.
    ///
    /// Nullable because a registrar may exist in the registry before their
    /// account is provisioned -- but without it they cannot authenticate.
    /// </summary>
    public string? ExternalSubjectId { get; set; }

    public required string DisplayName { get; set; }

    public RegistrarRole Role { get; set; }

    /// <summary>
    /// PBKDF2 hash of this registrar's offline device PIN, never the PIN
    /// itself. Facility devices download these so a nurse can unlock the
    /// device and register a birth while the post has no connectivity --
    /// the one thing Keycloak cannot cover, because a village post has no
    /// route to a token endpoint (Section 6.7).
    ///
    /// Null until a PIN is set: a registrar can exist in the registry before
    /// being issued one, and must simply be unable to work offline until
    /// they are.
    /// </summary>
    public string? CredentialHash { get; set; }

    /// <summary>
    /// Set when this person stopped working for the registry here. A withdrawn
    /// registrar resolves as unprovisioned on every request, and their PIN is
    /// left out of the bundle tablets download, so it stops unlocking them at
    /// each tablet's next sync. The row is kept, not deleted: every record they
    /// registered and every act in the audit trail still names them.
    /// </summary>
    public DateTime? WithdrawnAtUtc { get; set; }

    public string? WithdrawnReason { get; set; }

    public Guid? WithdrawnByRegistrarId { get; set; }

    public bool IsActive => WithdrawnAtUtc is null;
}
