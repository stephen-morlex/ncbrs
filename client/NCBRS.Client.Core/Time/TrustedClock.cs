using System.Diagnostics;

namespace NCBRS.Client.Time;

/// <summary>
/// A reading of a clock that only moves forward while the device stays on, and
/// that the date setting cannot touch (Android's elapsed real time). It restarts
/// at every boot, so a reading is only comparable with one from the same
/// <see cref="BootId"/>.
/// </summary>
public readonly record struct MonotonicReading(string BootId, TimeSpan Elapsed)
{
    /// <summary>How long since <paramref name="earlier"/>, or null if a reboot separates them.</summary>
    public TimeSpan? Since(MonotonicReading earlier)
        => earlier.BootId == BootId && Elapsed >= earlier.Elapsed ? Elapsed - earlier.Elapsed : null;
}

public interface IMonotonicClock
{
    MonotonicReading Read();
}

/// <summary>
/// The portable fallback: a stopwatch, and a new "boot" every time the app
/// starts. Conservative rather than wrong. A restart loses what the clock had
/// established and restarts a PIN cooldown in full, which never helps a thief.
/// The tablet uses Android's elapsed real time and boot count instead, which
/// survive the app being closed.
/// </summary>
public sealed class ProcessMonotonicClock : IMonotonicClock
{
    private readonly string _bootId = "process-" + Guid.NewGuid().ToString("N");
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public MonotonicReading Read() => new(_bootId, _elapsed.Elapsed);
}

/// <summary>A trusted time and the monotonic reading it was taken at.</summary>
public sealed record ClockMark(DateTime Utc, string BootId, TimeSpan Elapsed)
{
    public MonotonicReading Reading => new(BootId, Elapsed);

    /// <summary>This mark carried forward to <paramref name="now"/>, if no reboot separates them.</summary>
    public DateTime? At(MonotonicReading now) => now.Since(Reading) is { } elapsed ? Utc + elapsed : null;
}

/// <summary>
/// What the clock has established, kept in the encrypted store. <see cref="Last"/>
/// is the latest time the tablet trusted: nothing it reads afterwards may be
/// earlier. <see cref="Registry"/> is the registry's own time at the last
/// answer, which outranks everything while the tablet stays on.
/// </summary>
public sealed record ClockState(ClockMark? Last, ClockMark? Registry);

public enum ClockVerdict
{
    /// <summary>The tablet's clock agrees with everything trusted.</summary>
    Trusted,

    /// <summary>
    /// The tablet's clock is earlier than a time already trusted: set back, or
    /// reset by a flat battery. It is not used.
    /// </summary>
    Behind,

    /// <summary>The tablet's clock is ahead of the registry's time, carried forward. It is not used.</summary>
    Ahead,
}

/// <summary>The time to use, and whether the tablet's own clock could be.</summary>
/// <param name="Utc">The time to use for anything that matters.</param>
/// <param name="WallUtc">What the tablet's clock said, for telling the registrar what to fix.</param>
public sealed record ClockReading(DateTime Utc, ClockVerdict Verdict, DateTime WallUtc)
{
    public bool WallIsWrong => Verdict != ClockVerdict.Trusted;
}

/// <summary>
/// The time the tablet uses, where the date setting is something anyone holding
/// the tablet can change and a cheap tablet loses when its battery runs flat
/// (pilot-readiness §2).
///
/// <para>The rule is that <b>time never goes backward from what was already
/// trusted</b>, and only the registry may correct it in either direction.
/// Backward is the direction that matters: it would make an expired list of
/// withdrawn certificates read as current, and put a birth's capture time back
/// inside the statutory window. A clock behind a trusted time is detected,
/// reported and never used. While the tablet stays on, time since the last
/// trusted reading is measured on the monotonic clock, so a clock set back by
/// five minutes is caught to the minute, not just once it falls behind the
/// last reading.</para>
///
/// <para>A clock set <i>forward</i> without the registry is accepted: forward
/// is the safe direction for both of the above, and a tablet cannot tell it
/// from a registrar correcting a clock that was behind. The PIN lockout, the
/// one place forward helps a thief, never reads this clock (see
/// <c>OfflinePinLock</c>). A forward jump does raise what is trusted, and then
/// the registry's time corrects it at the next answer.</para>
///
/// <para>Pure apart from the two clocks it is given. It changes
/// <see cref="State"/>; the caller saves it with the rest of the device.</para>
/// </summary>
public sealed class TrustedClock(IMonotonicClock monotonic, Func<DateTime> wallUtc, ClockState? state = null)
{
    /// <summary>
    /// How far the tablet's clock may disagree before it is called wrong. Wider
    /// than a request's round trip (the registry's time comes from the HTTP
    /// <c>Date</c> header, to the second) and than ordinary drift between syncs.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    public ClockState State { get; private set; } = state ?? new ClockState(null, null);

    /// <summary>The time now, and whether the tablet's own clock agrees.</summary>
    public ClockReading Now()
    {
        var wall = AsUtc(wallUtc());
        var now = monotonic.Read();

        ClockReading reading;
        if (State.Registry?.At(now) is { } registry)
        {
            // The registry's time, carried forward on a clock nobody can set.
            // Either direction of disagreement is the tablet's clock being wrong.
            var drift = wall - registry;
            reading = drift.Duration() <= Tolerance
                ? new ClockReading(wall, ClockVerdict.Trusted, wall)
                : new ClockReading(registry, drift < TimeSpan.Zero ? ClockVerdict.Behind : ClockVerdict.Ahead, wall);
        }
        else
        {
            var floor = State.Last is { } last ? last.At(now) ?? last.Utc : (DateTime?)null;
            reading = floor is null || wall >= floor.Value - Tolerance
                ? new ClockReading(floor is { } f && f > wall ? f : wall, ClockVerdict.Trusted, wall)
                : new ClockReading(floor.Value, ClockVerdict.Behind, wall);
        }

        // Without the registry the reading is never below the floor, so the
        // floor only rises; with it, the registry's time decides.
        State = State with { Last = new ClockMark(reading.Utc, now.BootId, now.Elapsed) };
        return reading;
    }

    /// <summary>
    /// The registry's time, from an answer it gave. Authoritative in both
    /// directions: it replaces what the tablet trusted, including a time a
    /// clock set forward had pushed ahead.
    /// </summary>
    public void ObserveRegistry(DateTime registryUtc)
    {
        var now = monotonic.Read();
        var mark = new ClockMark(AsUtc(registryUtc), now.BootId, now.Elapsed);
        State = new ClockState(mark, mark);
    }

    private static DateTime AsUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
