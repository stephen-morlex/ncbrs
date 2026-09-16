namespace NCBRS.Services;

/// <summary>
/// The statutory registration window (draft Section 4.1).
///
/// This number is set in law, not by engineering. It is configuration
/// precisely because the legal amendment in Phase 0 decides it -- the draft
/// offers 30, 60 or 90 days as candidates -- and the platform must enforce
/// whatever the Act says without a code change.
/// </summary>
public class StatutoryRegistrationOptions
{
    public const string SectionName = "StatutoryRegistration";

    /// <summary>
    /// Days from birth within which a registration is on time. Defaults to
    /// 90, the most permissive of the draft's candidates, so that a
    /// deployment which has not yet configured it does not wrongly route
    /// ordinary registrations into the late process.
    /// </summary>
    public int WindowDays { get; set; } = 90;

    /// <summary>
    /// How far ahead of the server's clock a device's claimed capture time
    /// may be before it is refused.
    ///
    /// The window is measured to the moment a birth was captured on the
    /// device, not the moment it reached the server -- otherwise every
    /// registration from a village post offline for three weeks would be
    /// wrongly treated as late, which would turn the offline tier this
    /// system exists for into its heaviest administrative burden.
    ///
    /// That means trusting a device clock, and a wrong or manipulated one is
    /// the obvious way to dodge the late process. This bounds the forward
    /// direction; the backward direction is bounded by the birth date itself.
    /// The residual -- a device claiming a capture time between the birth and
    /// now -- is why the capture time is stored alongside the server's own
    /// receipt time, so the gap is visible to an auditor.
    /// </summary>
    public TimeSpan ClockSkewTolerance { get; set; } = TimeSpan.FromHours(12);
}
