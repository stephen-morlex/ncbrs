using NCBRS.Models;

namespace NCBRS.Client.Network;

/// <summary>
/// What the tablet needs to know about a facility to be handed over to it. The
/// centre's facility answer carries more (block health, thresholds); the fields
/// not named here are ignored when it is read.
/// </summary>
public sealed record FacilitySummary(Guid FacilityId, string Name, FacilityTier Tier, string CountyCode);
