namespace NCBRS.Models;

/// <summary>
/// A facility to bring into the registry. Its county is not supplied: it is
/// where <see cref="AdministrativeAreaId"/> sits in the tree. Its BRN range is
/// not supplied either: the registry allocates one that overlaps no other
/// facility's, because those ranges are what keeps every number unique while
/// devices issue them offline (decision #2).
/// </summary>
public record CreateFacilityRequest
{
    public string Name { get; init; } = string.Empty;

    public FacilityTier Tier { get; init; }

    public ConnectivityProfile ConnectivityProfile { get; init; }

    /// <summary>Where the facility is: a county, or a payam, boma or village within one.</summary>
    public Guid AdministrativeAreaId { get; init; }

    /// <summary>
    /// The registration office its BRNs will carry (<see cref="BrnFormat"/>):
    /// two to six capitals or digits, unique. Optional here and settable once
    /// later; until it has one, the facility issues numbers from its legacy
    /// numeric range.
    /// </summary>
    public string? OfficeCode { get; init; }
}

/// <summary>
/// Gives an existing facility its office code. Once only: issued numbers carry
/// the code, so it is never changed.
/// </summary>
public record SetOfficeCodeRequest
{
    public string OfficeCode { get; init; } = string.Empty;
}
