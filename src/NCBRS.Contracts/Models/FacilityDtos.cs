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

/// <summary>
/// Facilities to add from a CSV file (plan §17 item 38), checked first and added
/// only when asked and only if every row passes.
/// </summary>
public record ImportFacilitiesRequest
{
    /// <summary>
    /// The file's text. A header row naming the columns <c>name</c>, <c>tier</c>,
    /// <c>connectivity</c>, <c>area</c> (the area's code) and optionally
    /// <c>office_code</c>, in any order; then one facility per row.
    /// </summary>
    public string Csv { get; init; } = string.Empty;

    /// <summary>False checks the file and adds nothing. True adds every row, or none if any row fails.</summary>
    public bool Apply { get; init; }
}

/// <summary>What became, or would become, of each row.</summary>
public record ImportFacilitiesResponse(
    bool Applied,
    int Valid,
    int Invalid,
    IReadOnlyList<FacilityImportRow> Rows);

/// <param name="Line">The row's line in the file, counting the header as line 1.</param>
/// <param name="CountyCode">Where it would be placed, worked out from its area. Null when the area could not be resolved.</param>
/// <param name="FacilityId">Set once added.</param>
public record FacilityImportRow(
    int Line,
    string Name,
    string? AreaName,
    string? CountyCode,
    string? OfficeCode,
    IReadOnlyList<string> Problems,
    Guid? FacilityId,
    long? BrnBlockStart,
    long? BrnBlockEnd);
