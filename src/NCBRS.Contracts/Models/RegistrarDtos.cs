namespace NCBRS.Models;

/// <summary>
/// Bind a pending account (one that has signed in and declared itself) to a
/// facility and a role. The role must be one the account holds in Keycloak:
/// the registry records who someone is here, it does not grant what Keycloak
/// has not.
/// </summary>
public record BindRegistrarRequest
{
    /// <summary>The waiting account, by the registry's id for it (never its Keycloak subject).</summary>
    public Guid PendingAccountId { get; init; }

    public Guid FacilityId { get; init; }

    public RegistrarRole Role { get; init; }

    /// <summary>How the person is named in the registry. Defaults to the name on their account.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>Withdraw a registrar who has stopped working here. The reason is kept with them.</summary>
public record WithdrawRegistrarRequest
{
    public string Reason { get; init; } = string.Empty;
}
