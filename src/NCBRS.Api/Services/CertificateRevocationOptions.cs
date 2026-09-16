namespace NCBRS.Services;

/// <summary>
/// How long a published revocation list stays trustworthy.
/// </summary>
public class CertificateRevocationOptions
{
    public const string SectionName = "CertificateRevocation";

    /// <summary>
    /// The lifetime stamped into each list as NextUpdateUtc.
    ///
    /// This is a real trade-off, not a tuning knob. Short means an offline
    /// verifier goes "unknown" quickly and has to refuse certificates it
    /// cannot check -- a village office with a week-old copy turning people
    /// away. Long means a revoked certificate keeps passing for that long.
    ///
    /// Seven days is chosen for the offline tier: a device that syncs even
    /// once a week stays current, which matches the connectivity the rest of
    /// this system is designed around (design decision #1). A national
    /// deployment should set this from how often its worst-connected
    /// verifier actually reaches the registry, and not by guessing.
    /// </summary>
    public TimeSpan ValidFor { get; set; } = TimeSpan.FromDays(7);
}
