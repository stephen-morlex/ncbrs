using NCBRS.Client.Auth;
using NCBRS.Client.Time;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// Offline PIN unlock (WS-B3): the right PIN unlocks with no connectivity, and
/// wrong PINs are rate-limited locally so a stolen tablet is not an unbounded
/// brute-force target. The cooldown runs on the monotonic clock, so neither
/// the date setting nor a reboot can shorten it (pilot-readiness §2).
/// </summary>
public class OfflinePinLockTests
{
    // Low iterations keep the test fast; production uses the default.
    private static PinCredential Credential(string pin) => OfflinePinLock.CreateCredential(pin, iterations: 1000);

    private static readonly MonotonicReading Now = new("boot-1", TimeSpan.FromHours(3));

    private static MonotonicReading Later(TimeSpan by) => Now with { Elapsed = Now.Elapsed + by };

    private static OfflinePinLock LockedOut(OfflinePinLock pinLock, MonotonicReading at, int attempts = 3)
    {
        for (var i = 0; i < attempts; i++)
        {
            pinLock.Unlock("0000", at);
        }

        return pinLock;
    }

    [Fact]
    public void TheRightPinUnlocksAndClearsFailedAttempts()
    {
        var lockout = new OfflinePinLock(Credential("2468"), maxAttempts: 3, failedAttempts: 2, lockouts: 2);

        var result = lockout.Unlock("2468", Now);

        Assert.True(result.Unlocked);
        Assert.Equal(0, lockout.FailedAttempts);
        Assert.Equal(0, lockout.Lockouts);
        Assert.Null(lockout.LockedAt);
    }

    [Fact]
    public void AWrongPinDoesNotUnlockAndCountsDown()
    {
        var lockout = new OfflinePinLock(Credential("2468"), maxAttempts: 3);

        var first = lockout.Unlock("0000", Now);

        Assert.Equal(UnlockOutcome.WrongPin, first.Outcome);
        Assert.Equal(1, first.FailedAttempts);
        Assert.Equal(2, first.AttemptsRemaining);
    }

    [Fact]
    public void LocksOutAfterTheThresholdAndRefusesEvenTheRightPin()
    {
        var lockout = LockedOut(new OfflinePinLock(Credential("2468"), maxAttempts: 3, lockout: TimeSpan.FromMinutes(5)), Now);

        Assert.Equal(Now, lockout.LockedAt);
        Assert.Equal(TimeSpan.FromMinutes(5), lockout.LockedFor);

        // The right PIN, while locked, is still refused — and the window is not reset.
        var duringLock = lockout.Unlock("2468", Later(TimeSpan.FromMinutes(1)));
        Assert.Equal(UnlockOutcome.LockedOut, duringLock.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(4), duringLock.RetryAfter);
        Assert.Equal(Now, lockout.LockedAt);
    }

    [Fact]
    public void AfterTheCooldownAFreshWindowOpens()
    {
        var lockout = LockedOut(new OfflinePinLock(Credential("2468"), maxAttempts: 2, lockout: TimeSpan.FromMinutes(5)), Now, 2);

        var afterCooldown = lockout.Unlock("2468", Later(TimeSpan.FromMinutes(6)));

        Assert.True(afterCooldown.Unlocked);
        Assert.Equal(0, lockout.FailedAttempts);
    }

    /// <summary>
    /// The attack pilot-readiness §2 found: the lock never sees the date, so
    /// moving it forward a day changes nothing. Only time the tablet was
    /// actually on counts.
    /// </summary>
    [Fact]
    public void SettingTheDateForwardDoesNotEndTheCooldown()
    {
        var lockout = LockedOut(new OfflinePinLock(Credential("2468"), maxAttempts: 3), Now);

        // Seconds later on the monotonic clock, whatever the date now says.
        Assert.Equal(UnlockOutcome.LockedOut, lockout.Unlock("2468", Later(TimeSpan.FromSeconds(30))).Outcome);
    }

    [Fact]
    public void ARebootRestartsTheCooldownRatherThanEndingIt()
    {
        var lockout = LockedOut(new OfflinePinLock(Credential("2468"), maxAttempts: 3, lockout: TimeSpan.FromMinutes(5)), Now);

        var afterReboot = new MonotonicReading("boot-2", TimeSpan.FromSeconds(40));
        var first = lockout.Unlock("2468", afterReboot);

        Assert.Equal(UnlockOutcome.LockedOut, first.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(5), first.RetryAfter);
        Assert.Equal(afterReboot, lockout.LockedAt);

        // Measured from the reboot, the full cooldown then ends as usual.
        Assert.True(lockout.Unlock("2468", afterReboot with { Elapsed = afterReboot.Elapsed + TimeSpan.FromMinutes(5) }).Unlocked);
    }

    [Fact]
    public void EachLockoutIsTwiceTheLastUpToAnHour()
    {
        var lockout = new OfflinePinLock(Credential("2468"), maxAttempts: 3, lockout: TimeSpan.FromMinutes(5));
        var at = Now;
        var cooldowns = new List<TimeSpan>();

        for (var round = 0; round < 6; round++)
        {
            LockedOut(lockout, at);
            cooldowns.Add(lockout.LockedFor!.Value);
            at = at with { Elapsed = at.Elapsed + lockout.LockedFor.Value };
        }

        Assert.Equal(
            new[] { 5, 10, 20, 40, 60, 60 }.Select(minutes => TimeSpan.FromMinutes(minutes)),
            cooldowns);
    }

    [Fact]
    public void TheRightPinResetsTheEscalation()
    {
        var lockout = LockedOut(new OfflinePinLock(Credential("2468"), maxAttempts: 3, lockout: TimeSpan.FromMinutes(5)), Now);
        var after = Later(TimeSpan.FromMinutes(5));
        Assert.True(lockout.Unlock("2468", after).Unlocked);

        LockedOut(lockout, after);

        Assert.Equal(TimeSpan.FromMinutes(5), lockout.LockedFor);
    }

    [Fact]
    public void RateLimitStateSurvivesRestartWhenRestored()
    {
        // Force-quitting between guesses must not reset the counter: the caller
        // restores the persisted state.
        var restored = new OfflinePinLock(Credential("2468"), maxAttempts: 3, failedAttempts: 2);

        var result = restored.Unlock("0000", Now);

        Assert.Equal(UnlockOutcome.LockedOut, result.Outcome);
    }

    [Fact]
    public void AnEmptyPinNeverUnlocks()
    {
        var lockout = new OfflinePinLock(Credential("2468"), maxAttempts: 5);

        Assert.Equal(UnlockOutcome.WrongPin, lockout.Unlock("", Now).Outcome);
        Assert.Equal(UnlockOutcome.WrongPin, lockout.Unlock(null, Now).Outcome);
    }
}
