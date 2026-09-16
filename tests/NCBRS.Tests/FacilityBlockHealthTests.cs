using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// W2's block-exhaustion warning.
///
/// The arithmetic is the part that fails silently: an off-by-one here reports
/// a facility as holding a number it cannot actually issue, and the first
/// symptom is a device handing out a `PROV-` identifier nobody expected.
/// </summary>
public class FacilityBlockHealthTests
{
    private static readonly BrnBlockOptions Options = new();

    // ---- what "remaining" means ------------------------------------------

    [Fact]
    public void Remaining_counts_the_numbers_still_available_to_issue()
    {
        // 100..199 granted, 150 is the first not yet handed out, so 150..199
        // remain: fifty numbers, inclusive of both ends.
        var facility = Facility(start: 100, end: 199, next: 150);

        Assert.Equal(50, BrnBlockHealth.Remaining(facility));
    }

    [Fact]
    public void A_block_with_one_number_left_reports_one()
    {
        // The boundary the inclusive arithmetic exists for: next == end means
        // that number is still issuable.
        var facility = Facility(start: 100, end: 199, next: 199);

        Assert.Equal(1, BrnBlockHealth.Remaining(facility));
    }

    [Fact]
    public void A_fully_consumed_block_reports_none()
    {
        // next is past the end: nothing left.
        var facility = Facility(start: 100, end: 199, next: 200);

        Assert.Equal(0, BrnBlockHealth.Remaining(facility));
    }

    [Fact]
    public void A_facility_that_was_never_granted_a_block_has_none()
    {
        // All three fields at zero. The arithmetic alone would say one number
        // is available, and a device acting on that would issue a BRN the
        // centre never granted -- which BrnReconciler would then refuse,
        // leaving the record provisional for a reason nobody could explain.
        var facility = Facility(start: 0, end: 0, next: 0);

        Assert.Equal(0, BrnBlockHealth.Remaining(facility));
        Assert.Equal(BrnBlockStatus.Exhausted, BrnBlockHealth.StatusOf(facility, Options));
    }

    // ---- "low" is not one number -----------------------------------------

    [Fact]
    public void The_same_remaining_count_is_healthy_for_a_hospital_and_low_for_a_village_post()
    {
        // 100 left. A hospital can ask for more this afternoon. A post a
        // fortnight from its next connection cannot, and will start issuing
        // provisional identifiers before anyone hears from it.
        var hospital = Facility(start: 1, end: 1000, next: 901, ConnectivityProfile.AlwaysOn);
        var post = Facility(start: 1, end: 1000, next: 901, ConnectivityProfile.OfflineFirst);

        Assert.Equal(100, BrnBlockHealth.Remaining(hospital));
        Assert.Equal(100, BrnBlockHealth.Remaining(post));

        Assert.Equal(BrnBlockStatus.Healthy, BrnBlockHealth.StatusOf(hospital, Options));
        Assert.Equal(BrnBlockStatus.Low, BrnBlockHealth.StatusOf(post, Options));
    }

    [Theory]
    [InlineData(ConnectivityProfile.AlwaysOn, 50)]
    [InlineData(ConnectivityProfile.Intermittent, 200)]
    [InlineData(ConnectivityProfile.OfflineFirst, 500)]
    public void Each_profile_is_judged_against_its_own_threshold(
        ConnectivityProfile profile, int threshold)
    {
        Assert.Equal(threshold, Options.WarnBelow(profile));

        // Exactly at the threshold is already low -- the warning is meant to
        // arrive before the number is reached, not as it is passed.
        var at = Facility(start: 1, end: 10_000, next: 10_000 - threshold + 1, profile);
        var above = Facility(start: 1, end: 10_000, next: 10_000 - threshold, profile);

        Assert.Equal(BrnBlockStatus.Low, BrnBlockHealth.StatusOf(at, Options));
        Assert.Equal(BrnBlockStatus.Healthy, BrnBlockHealth.StatusOf(above, Options));
    }

    [Fact]
    public void Exhausted_outranks_low()
    {
        // An offline post with nothing left is not "low" -- its devices are
        // already issuing PROV- identifiers, and the two states call for
        // different acts.
        var post = Facility(start: 1, end: 100, next: 101, ConnectivityProfile.OfflineFirst);

        Assert.Equal(BrnBlockStatus.Exhausted, BrnBlockHealth.StatusOf(post, Options));
    }

    [Fact]
    public void A_hospital_with_a_healthy_block_is_not_flagged()
    {
        // The other failure mode: thresholds tuned for a village post would
        // fill a district's screen with warnings about hospitals working
        // exactly as designed, and a queue that is mostly noise stops being
        // read.
        var hospital = Facility(start: 1, end: 10_000, next: 1_000, ConnectivityProfile.AlwaysOn);

        Assert.Equal(BrnBlockStatus.Healthy, BrnBlockHealth.StatusOf(hospital, Options));
    }

    private static Facility Facility(
        long start,
        long end,
        long next,
        ConnectivityProfile profile = ConnectivityProfile.OfflineFirst)
        => new()
        {
            Name = "Test facility",
            DistrictId = "D-CENTRAL-07",
            ConnectivityProfile = profile,
            BrnBlockStart = start,
            BrnBlockEnd = end,
            BrnBlockNextAvailable = next,
        };
}
