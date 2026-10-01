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
}
