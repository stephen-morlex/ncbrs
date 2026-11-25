using NCBRS.Client.Time;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The tablet's clock (pilot-readiness §2): time never goes backward from what
/// was already trusted, and only the registry corrects it in either direction.
/// </summary>
public class TrustedClockTests
{
    private static readonly DateTime Noon = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Clocks : IMonotonicClock
    {
        public DateTime Wall = Noon;
        public MonotonicReading Monotonic = new("boot-1", TimeSpan.FromHours(1));

        public MonotonicReading Read() => Monotonic;

        /// <summary>Time passes: both clocks move together.</summary>
        public void Pass(TimeSpan span)
        {
            Wall += span;
            Monotonic = Monotonic with { Elapsed = Monotonic.Elapsed + span };
        }

        public void Reboot(TimeSpan offFor)
        {
            Wall += offFor;
            Monotonic = new MonotonicReading(Monotonic.BootId + "+", TimeSpan.FromSeconds(30));
        }

        public TrustedClock Clock(ClockState? state = null) => new(this, () => Wall, state);
    }

    [Fact]
    public void AFreshTabletTrustsItsOwnClock()
    {
        var clocks = new Clocks();

        var reading = clocks.Clock().Now();

        Assert.Equal(ClockVerdict.Trusted, reading.Verdict);
        Assert.Equal(Noon, reading.Utc);
    }

    /// <summary>
    /// The certificate-check attack: set the date back and a week-old list of
    /// withdrawn certificates reads as current. The set-back clock is caught and
    /// the time carries on from what was trusted, measured on a clock the date
    /// setting cannot touch.
    /// </summary>
    [Fact]
    public void AClockSetBackIsDetectedAndNotUsed()
    {
        var clocks = new Clocks();
        var clock = clocks.Clock();
        clock.Now();

        clocks.Pass(TimeSpan.FromMinutes(30));
        clocks.Wall -= TimeSpan.FromDays(7);
        var reading = clock.Now();

        Assert.Equal(ClockVerdict.Behind, reading.Verdict);
        Assert.Equal(Noon.AddMinutes(30), reading.Utc);
        Assert.Equal(Noon.AddMinutes(30).AddDays(-7), reading.WallUtc);
    }

    /// <summary>A flat battery: the clock comes back at its factory date, and the tablet does not believe it.</summary>
    [Fact]
    public void AClockResetAcrossARebootIsDetected()
    {
        var clocks = new Clocks();
        var clock = clocks.Clock();
        clock.Now();

        clocks.Reboot(TimeSpan.FromHours(10));
        clocks.Wall = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var reading = clocks.Clock(clock.State).Now();

        Assert.Equal(ClockVerdict.Behind, reading.Verdict);
        Assert.Equal(Noon, reading.Utc); // the last trusted time: nothing later is known
    }

    [Fact]
    public void AClockCorrectedForwardIsAccepted()
    {
        var clocks = new Clocks();
        var clock = clocks.Clock();
        clock.Now();

        clocks.Wall += TimeSpan.FromDays(3);
        var reading = clock.Now();

        Assert.Equal(ClockVerdict.Trusted, reading.Verdict);
        Assert.Equal(Noon.AddDays(3), reading.Utc);
    }

    [Fact]
    public void SmallDriftIsNotCalledWrong()
    {
        var clocks = new Clocks();
        var clock = clocks.Clock();
        clock.Now();

        clocks.Pass(TimeSpan.FromMinutes(10));
        clocks.Wall -= TimeSpan.FromSeconds(90);

        Assert.Equal(ClockVerdict.Trusted, clock.Now().Verdict);
    }

    [Fact]
    public void TheRegistrysTimeOutranksTheTabletsInBothDirections()
    {
        var clocks = new Clocks();
        var clock = clocks.Clock();
        clocks.Wall = Noon.AddHours(5); // set forward
        clock.Now();

        clock.ObserveRegistry(Noon);
        var ahead = clock.Now();
        Assert.Equal(ClockVerdict.Ahead, ahead.Verdict);
        Assert.Equal(Noon, ahead.Utc);

        clocks.Pass(TimeSpan.FromMinutes(20));
        clocks.Wall = Noon.AddMinutes(20);
        var corrected = clock.Now();
        Assert.Equal(ClockVerdict.Trusted, corrected.Verdict);
        Assert.Equal(Noon.AddMinutes(20), corrected.Utc);

        clocks.Wall = Noon.AddDays(-1);
        Assert.Equal(ClockVerdict.Behind, clock.Now().Verdict);
    }

    /// <summary>
    /// A forward jump raises what is trusted, so a later correction back reads
    /// as "behind" until the registry answers. That is the cost of accepting
    /// forward jumps, and the registry clears it.
    /// </summary>
    [Fact]
    public void TheRegistryUndoesAForwardJump()
    {
        var clocks = new Clocks();
        var clock = clocks.Clock();
        clocks.Wall = Noon.AddDays(30);
        clock.Now();

        clocks.Wall = Noon;
        Assert.Equal(ClockVerdict.Behind, clock.Now().Verdict);

        clock.ObserveRegistry(Noon);
        Assert.Equal(ClockVerdict.Trusted, clock.Now().Verdict);
    }

    [Fact]
    public void TheRegistrysTimeIsCarriedForwardOnlyWithinABoot()
    {
        var clocks = new Clocks();
        var clock = clocks.Clock();
        clock.ObserveRegistry(Noon);

        clocks.Reboot(TimeSpan.FromHours(2));
        var reading = clocks.Clock(clock.State).Now();

        // Off for two hours: the registry's time cannot be carried across, and
        // the tablet's clock, later than anything trusted, is believed.
        Assert.Equal(ClockVerdict.Trusted, reading.Verdict);
        Assert.Equal(Noon.AddHours(2), reading.Utc);
    }

    [Fact]
    public void TheStateRoundTripsThroughTheStore()
    {
        var clocks = new Clocks();
        var clock = clocks.Clock();
        clock.ObserveRegistry(Noon);
        clock.Now();

        var json = System.Text.Json.JsonSerializer.Serialize(clock.State);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ClockState>(json);

        Assert.Equal(clock.State, restored);
    }
}
