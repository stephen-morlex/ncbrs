using System.ComponentModel.DataAnnotations;

namespace NCBRS.Models;

public enum FacilityTier
{
    Hospital,
    Clinic,
    VillageHealthPost
}

public enum ConnectivityProfile
{
    AlwaysOn,
    Intermittent,
    OfflineFirst
}

public class Facility
{
    public Guid FacilityId { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public FacilityTier Tier { get; set; }

    public required string DistrictId { get; set; }

    public ConnectivityProfile ConnectivityProfile { get; set; }

    /// <summary>
    /// The current unused-BRN block boundaries reserved for this facility's
    /// device(s). See Section 6.3/6.6 of the NCBRS draft: this is what lets a
    /// village post issue unique registration numbers for weeks while offline.
    /// </summary>
    public long BrnBlockStart { get; set; }
    public long BrnBlockEnd { get; set; }

    /// <summary>
    /// Concurrency-checked: EF includes the originally-read value of this
    /// column in the UPDATE's WHERE clause, so two concurrent block requests
    /// for the same facility can never both succeed against the same
    /// starting value -- the loser gets a DbUpdateConcurrencyException
    /// instead of silently handing out an overlapping BRN range. See
    /// BirthRecordsController.RequestBrnBlock.
    /// </summary>
    [ConcurrencyCheck]
    public long BrnBlockNextAvailable { get; set; }
}
