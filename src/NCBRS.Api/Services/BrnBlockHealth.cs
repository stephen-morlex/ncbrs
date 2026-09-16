using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// How close a facility is to running out of the registration numbers it was
/// granted.
///
/// **This is the question W2 exists to answer, and it is not "how many are
/// left".** A device that exhausts its block while offline issues a
/// `PROV-` identifier instead of a BRN (draft 6.3): the birth is still
/// registered, but the family leaves with a slip rather than a certificate,
/// and the record waits for a central act before it can be certificated at
/// all. Seeing that coming is the difference between topping up a block and
/// explaining to a district why forty families are holding provisional
/// paper.
///
/// **"Low" is not one number, for the same reason a silent device is not one
/// number of days.** A hospital on <see cref="ConnectivityProfile.AlwaysOn"/>
/// with fifty numbers left is fine — it can ask for more the moment it needs
/// them. A village post on <see cref="ConnectivityProfile.OfflineFirst"/>
/// with fifty left, a fortnight from its next connection, is already in
/// trouble. Applying the hospital's threshold to the post is how the post
/// runs out; applying the post's to the hospital fills a district's screen
/// with warnings about facilities that are working exactly as designed.
///
/// The thresholds mirror <c>DeviceSilenceOptions</c> deliberately: same
/// reasoning, same shape, so a reader who understands one understands the
/// other.
/// </summary>
public class BrnBlockOptions
{
    public const string SectionName = "BrnBlock";

    /// <summary>
    /// Can request a new block whenever it likes, so it needs only enough to
    /// cover the gap between noticing and asking.
    /// </summary>
    public int AlwaysOnRemaining { get; set; } = 50;

    /// <summary>Connects most days; a few days of registrations.</summary>
    public int IntermittentRemaining { get; set; } = 200;

    /// <summary>
    /// Weeks between connections. The buffer has to cover the whole gap,
    /// because there is no asking for more in the middle of it.
    /// </summary>
    public int OfflineFirstRemaining { get; set; } = 500;

    /// <summary>
    /// Below this, a facility is not approaching exhaustion — it is out, and
    /// its devices are already issuing provisional identifiers.
    /// </summary>
    public int ExhaustedRemaining { get; set; }

    public int WarnBelow(ConnectivityProfile profile) => profile switch
    {
        ConnectivityProfile.AlwaysOn => AlwaysOnRemaining,
        ConnectivityProfile.Intermittent => IntermittentRemaining,
        _ => OfflineFirstRemaining,
    };
}

/// <summary>
/// Three states rather than a percentage, because the action differs at each
/// and a percentage invites a reader to invent their own threshold.
/// </summary>
public enum BrnBlockStatus
{
    /// <summary>Enough for this facility's connectivity. Nothing to do.</summary>
    Healthy,

    /// <summary>
    /// Enough today, not enough to be confident through the next offline
    /// stretch. Grant a block now, while it is a routine act.
    /// </summary>
    Low,

    /// <summary>
    /// Out. Devices are issuing `PROV-` identifiers, families are leaving
    /// with slips, and those records cannot be certificated until the centre
    /// extends the range.
    /// </summary>
    Exhausted,
}

public static class BrnBlockHealth
{
    /// <summary>
    /// Numbers still available to hand to a device.
    ///
    /// `BrnBlockNextAvailable` is the first number **not yet granted**, so the
    /// remaining count is inclusive of the end and exclusive of next — the
    /// same arithmetic `BrnReconciler` uses when deciding whether a submitted
    /// BRN was one this facility could legitimately have issued. Getting it
    /// off by one here would report a facility as having a number it cannot
    /// actually give out.
    /// </summary>
    public static long Remaining(Facility facility)
    {
        var remaining = facility.BrnBlockEnd - facility.BrnBlockNextAvailable + 1;

        // A facility that has never been granted a block has all three fields
        // at zero, which the arithmetic above would report as one number
        // available. It has none.
        if (facility.BrnBlockEnd <= 0)
        {
            return 0;
        }

        return remaining > 0 ? remaining : 0;
    }

    public static BrnBlockStatus StatusOf(Facility facility, BrnBlockOptions options)
    {
        var remaining = Remaining(facility);

        if (remaining <= options.ExhaustedRemaining)
        {
            return BrnBlockStatus.Exhausted;
        }

        return remaining <= options.WarnBelow(facility.ConnectivityProfile)
            ? BrnBlockStatus.Low
            : BrnBlockStatus.Healthy;
    }
}
