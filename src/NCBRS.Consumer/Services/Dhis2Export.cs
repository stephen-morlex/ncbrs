namespace NCBRS.Consumer.Services;

/// <summary>
/// Which DHIS2 data element each figure is reported against.
///
/// UIDs are instance-specific — the Ministry's DHIS2 generates its own — so
/// they are configuration, never constants. An export hardcoded to one
/// instance's UIDs silently reports nothing on any other, and "nothing" in
/// DHIS2 looks exactly like a period with no births.
/// </summary>
public class Dhis2ExportOptions
{
    public const string SectionName = "Dhis2Export";

    public string? LiveBirths { get; set; }
    public string? LiveBirthsMale { get; set; }
    public string? LiveBirthsFemale { get; set; }
    public string? FetalDeaths { get; set; }
    public string? NeonatalDeaths { get; set; }
    public string? MaternalDeaths { get; set; }
    public string? RegisteredWithinWindow { get; set; }

    /// <summary>
    /// Maps a district id to its DHIS2 organisation unit. A district with no
    /// mapping is not exported: guessing an org unit would file a district's
    /// births against someone else's.
    /// </summary>
    public Dictionary<string, string> OrgUnits { get; set; } = [];

    /// <summary>
    /// The smallest count that may leave the registry.
    ///
    /// **Aggregate is not the same as anonymous.** A count of one, in a small
    /// area, for a rare event, identifies a family — and the rarest events
    /// here are a stillbirth and a mother who died. Anyone who knows there was
    /// one such birth in their district that month learns the rest from the
    /// export.
    ///
    /// Five is the threshold most statistical agencies settle on for health
    /// data. It is deliberately not zero, and it is deliberately configurable
    /// upward rather than downward.
    /// </summary>
    public int MinimumCellSize { get; set; } = 5;
}

/// <summary>
/// One DHIS2 data value, in the shape its dataValueSets endpoint accepts.
///
/// Note what is absent and must stay absent: no BRN, no name, no record or
/// facility identifier, no date of birth. The unit of this payload is a
/// district-month, not a person.
/// </summary>
public record Dhis2DataValue(string DataElement, string Period, string OrgUnit, string Value);

public record Dhis2DataValueSet(string Period, IReadOnlyList<Dhis2DataValue> DataValues);

/// <summary>
/// What was held back, and why. Returned alongside the export rather than
/// logged quietly: a recipient who cannot tell a suppressed figure from an
/// absent one will read the gap as zero, and zero stillbirths is a
/// materially different claim from "too few to publish safely".
/// </summary>
public record Dhis2Suppression(string OrgUnit, string Reason);

public record Dhis2Export(
    Dhis2DataValueSet DataValueSet,
    IReadOnlyList<Dhis2Suppression> Suppressed,

    /// <summary>
    /// Districts with no DHIS2 mapping. Reported, not skipped silently — an
    /// unmapped district is a district whose births never reach the national
    /// figures, and nothing else would reveal it.
    /// </summary>
    IReadOnlyList<string> Unmapped);
