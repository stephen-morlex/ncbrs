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

/// <summary>
/// Staff to bind from a CSV file (plan §17 item 38): accounts already waiting in
/// the queue, each to a facility and a role. Checked first; bound only when
/// asked and only if every row passes.
/// </summary>
public record ImportRegistrarsRequest
{
    /// <summary>
    /// The file's text: a header row naming <c>username</c>, <c>facility</c>
    /// (its office code, or its name) and <c>role</c>, and optionally
    /// <c>display_name</c>; then one person per row.
    /// </summary>
    public string Csv { get; init; } = string.Empty;

    /// <summary>False checks the file and binds nobody. True binds every row, or nobody if any row fails.</summary>
    public bool Apply { get; init; }
}

public record ImportRegistrarsResponse(
    bool Applied,
    int Valid,
    int Invalid,
    IReadOnlyList<RegistrarImportRow> Rows);

/// <param name="Line">The row's line in the file, counting the header as line 1.</param>
/// <param name="RegistrarId">Set once bound.</param>
public record RegistrarImportRow(
    int Line,
    string Username,
    string? DisplayName,
    string? FacilityName,
    RegistrarRole? Role,
    IReadOnlyList<string> Problems,
    Guid? RegistrarId);
