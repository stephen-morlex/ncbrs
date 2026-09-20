using NCBRS.Client.Auth;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// Offline PIN unlock (WS-B3): the right PIN unlocks with no connectivity, and
/// wrong PINs are rate-limited locally so a stolen tablet is not an unbounded
/// brute-force target.
/// </summary>
public class OfflinePinLockTests
{
    // Low iterations keep the test fast; production uses the default.
    private static PinCredential Credential(string pin) => OfflinePinLock.CreateCredential(pin, iterations: 1000);

    private static readonly DateTime Now = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheRightPinUnlocksAndClearsFailedAttempts()
    {
        var lockout = new OfflinePinLock(Credential("2468"), maxAttempts: 3, failedAttempts: 2);

        var result = lockout.Unlock("2468", Now);

        Assert.True(result.Unlocked);
        Assert.Equal(0, lockout.FailedAttempts);
        Assert.Null(lockout.LockedUntilUtc);
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
        var lockout = new OfflinePinLock(Credential("2468"), maxAttempts: 3, lockout: TimeSpan.FromMinutes(5));

        lockout.Unlock("0000", Now);
        lockout.Unlock("0000", Now);
        var third = lockout.Unlock("0000", Now);

        Assert.Equal(UnlockOutcome.LockedOut, third.Outcome);
        Assert.Equal(Now.AddMinutes(5), lockout.LockedUntilUtc);

        // The right PIN, while locked, is still refused — and the window is not reset.
        var duringLock = lockout.Unlock("2468", Now.AddMinutes(1));
        Assert.Equal(UnlockOutcome.LockedOut, duringLock.Outcome);
        Assert.Equal(Now.AddMinutes(5), lockout.LockedUntilUtc);
    }

    [Fact]
    public void AfterTheCooldownAFreshWindowOpens()
    {
        var lockout = new OfflinePinLock(Credential("2468"), maxAttempts: 2, lockout: TimeSpan.FromMinutes(5));
        lockout.Unlock("0000", Now);
        lockout.Unlock("0000", Now); // locked until Now+5

        var afterCooldown = lockout.Unlock("2468", Now.AddMinutes(6));

        Assert.True(afterCooldown.Unlocked);
        Assert.Equal(0, lockout.FailedAttempts);
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
