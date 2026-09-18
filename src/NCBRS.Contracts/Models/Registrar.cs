namespace NCBRS.Models;

public enum RegistrarRole
{
    FacilityRegistrar,
    CommunityHealthWorker,
    DistrictOfficer,
    MinistryAdmin
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
}
