namespace NCBRS.Models;

/// <summary>
/// An identity-provider account that has signed in but is not yet bound to a
/// registrar (pilot readiness §1). It declares itself: `GET /api/me` records it
/// from its own token, so a district officer binds it by picking it from a
/// queue rather than copying an id out of Keycloak, and the registry needs no
/// Keycloak admin credential.
///
/// What is held is what the token said at the last sign-in: enough for an
/// officer to recognise a colleague and see which roles and county the account
/// was given, and nothing more. The row is removed when the account is bound;
/// the binding itself is audited.
/// </summary>
public class PendingAccount
{
    /// <summary>
    /// What the registry calls this waiting account, and what an officer binds
    /// by. Not the subject: that identifies the account to Keycloak and is
    /// never published, as the registrar directory has always held.
    /// </summary>
    public Guid PendingAccountId { get; set; } = Guid.CreateVersion7();

    /// <summary>The identity provider's subject (Keycloak's "sub"). Never published.</summary>
    public required string Subject { get; set; }

    public required string DisplayName { get; set; }

    public string? Username { get; set; }

    public string? Email { get; set; }

    /// <summary>The account's realm roles, comma-separated. A binding may only give one of them.</summary>
    public string RealmRoles { get; set; } = string.Empty;

    /// <summary>The county group the account was placed in, if exactly one; null otherwise.</summary>
    public string? CountyCode { get; set; }

    public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}
