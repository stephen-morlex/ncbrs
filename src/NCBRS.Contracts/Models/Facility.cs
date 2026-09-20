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

    /// <summary>
    /// The facility's county code — the flat, indexed scope-and-reporting key
    /// (formerly <c>DistrictId</c>). It is a denormalisation of the county the
    /// hierarchy resolves to: scope filters and reporting group on it in SQL,
    /// which a variable-depth walk up <see cref="AdministrativeAreaId"/> cannot
    /// do. It must equal the county <see cref="AdministrativeArea"/> resolves
    /// to; the two are kept consistent when a facility is placed.
    /// </summary>
    public required string CountyCode { get; set; }

    /// <summary>
    /// Where this facility sits in the administrative hierarchy — usually a
    /// payam or boma. The precise location; the county and state a record is
    /// scoped and reported under can also be resolved by walking up from here.
    /// Nullable only for the length of the transition; every facility is
    /// expected to have one.
    /// </summary>
    public Guid? AdministrativeAreaId { get; set; }

    public AdministrativeArea? AdministrativeArea { get; set; }

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
