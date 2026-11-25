using System.Security.Cryptography;
using System.Text;
using NCBRS.Client.Time;

namespace NCBRS.Client.Auth;

/// <summary>
/// The PIN credential provisioned to the device while it had connectivity, so
/// the registrar can be checked offline. Only the salted PBKDF2 hash is stored —
/// never the PIN — because the device this sits on is exactly what a thief holds.
/// </summary>
public sealed record PinCredential(string SaltBase64, int Iterations, string HashBase64)
{
    private const string ServerAlgorithm = "pbkdf2-sha256";

    /// <summary>
    /// A credential from the centre's self-describing hash
    /// (<c>pbkdf2-sha256$iterations$salt$hash</c>, as the centre stores a PIN a
    /// registrar set there). The same derivation the lock verifies with, so a
    /// PIN set at the centre unlocks the tablet. Null for a format this device
    /// cannot verify: an entry it cannot check must never be one that unlocks.
    /// </summary>
    public static PinCredential? FromServerHash(string? hash)
    {
        var parts = hash?.Split('$');
        if (parts is not [ServerAlgorithm, var iterations, var salt, var derived]
            || !int.TryParse(iterations, out var count) || count < 1)
        {
            return null;
        }

        try
        {
            Convert.FromBase64String(salt);
            Convert.FromBase64String(derived);
        }
        catch (FormatException)
        {
            return null;
        }

        return new PinCredential(salt, count, derived);
    }
}

public enum UnlockOutcome
{
    Unlocked,
    WrongPin,
    LockedOut
}

public sealed record UnlockResult(
    UnlockOutcome Outcome, int FailedAttempts, TimeSpan? RetryAfter, int AttemptsRemaining)
{
    public bool Unlocked => Outcome == UnlockOutcome.Unlocked;
}

/// <summary>
/// WS-B3. Unlocking the device with a PIN when there is no network to check a
/// password against.
///
/// The credential is a salted PBKDF2 hash provisioned online; unlock verifies
/// against it with no connectivity at all. The rate limit is the point: a
/// stolen tablet is an offline brute-force target, so wrong PINs are counted
/// and, past a threshold, locked out for a cooldown — <b>locally</b>, since
/// there is by definition no server to enforce it. While locked, even the right
/// PIN is refused; the lock is checked before the PIN is, so guessing cannot
/// reset the window.
///
/// <para><b>The cooldown is measured on the monotonic clock, never the date
/// setting</b> (pilot-readiness §2). Measured on the wall clock, setting the
/// date forward five minutes reopened the window. The monotonic clock restarts
/// at a reboot, so a reboot <b>restarts the cooldown in full</b>: rebooting can
/// only lengthen a lockout, never end one.</para>
///
/// <para><b>Each lockout is twice the last</b> (5, 10, 20, 40 minutes, then an
/// hour each) until the right PIN is entered. A fixed cooldown let a thief try
/// five PINs every five minutes indefinitely. Capped at an hour, a six-digit PIN
/// is still years out of reach, and a registrar who mistypes is never locked
/// out for long.</para>
///
/// A pure state machine: the caller persists its state to the encrypted store
/// (B2) after every attempt, so the limit survives the obvious attack of
/// force-quitting between guesses.
/// </summary>
public sealed class OfflinePinLock
{
    private const int DefaultIterations = 100_000;
    public static readonly TimeSpan DefaultLockout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumLockout = TimeSpan.FromHours(1);

    private readonly PinCredential _credential;
    private readonly int _maxAttempts;
    private readonly TimeSpan _lockout;

    public OfflinePinLock(
        PinCredential credential,
        int maxAttempts = 5,
        TimeSpan lockout = default,
        int failedAttempts = 0,
        int lockouts = 0,
        MonotonicReading? lockedAt = null,
        TimeSpan? lockedFor = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one attempt must be allowed.");
        }

        _credential = credential;
        _maxAttempts = maxAttempts;
        _lockout = lockout <= TimeSpan.Zero ? DefaultLockout : lockout;
        FailedAttempts = failedAttempts < 0 ? 0 : failedAttempts;
        Lockouts = lockouts < 0 ? 0 : lockouts;
        if (lockedAt is not null)
        {
            LockedAt = lockedAt;
            LockedFor = lockedFor is { } span && span > TimeSpan.Zero ? span : _lockout;
        }
    }

    public int FailedAttempts { get; private set; }

    /// <summary>Lockouts since the right PIN was last entered: what the next cooldown doubles from.</summary>
    public int Lockouts { get; private set; }

    /// <summary>When the current cooldown began, on the monotonic clock. Null when not locked.</summary>
    public MonotonicReading? LockedAt { get; private set; }

    /// <summary>How long the current cooldown lasts. Null when not locked.</summary>
    public TimeSpan? LockedFor { get; private set; }

    /// <summary>The cooldown after <paramref name="lockouts"/> earlier ones: doubled each time, capped.</summary>
    public TimeSpan CooldownAfter(int lockouts)
    {
        var span = _lockout;
        for (var i = 0; i < lockouts && span < MaximumLockout; i++)
        {
            span += span;
        }

        return span < MaximumLockout ? span : MaximumLockout;
    }

    /// <summary>Provision a credential from a PIN, done online. The PIN is not retained.</summary>
    public static PinCredential CreateCredential(string pin, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(pin))
        {
            throw new ArgumentException("A PIN is required.", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(pin, salt, iterations);

        return new PinCredential(Convert.ToBase64String(salt), iterations, Convert.ToBase64String(hash));
    }

    public UnlockResult Unlock(string? pin, MonotonicReading now)
    {
        // Locked check first: guessing must not even reach the comparison while
        // the cooldown is running, let alone reset it.
        if (LockedAt is { } at && LockedFor is { } span)
        {
            if (now.Since(at) is not { } elapsed)
            {
                // A reboot since the lock began: its progress cannot be
                // measured, so the cooldown starts again.
                LockedAt = now;
                return Result(UnlockOutcome.LockedOut, span);
            }

            if (elapsed < span)
            {
                return Result(UnlockOutcome.LockedOut, span - elapsed);
            }

            // A cooldown that has elapsed opens a fresh window. The count of
            // lockouts is kept, so the next one is longer.
            LockedAt = null;
            LockedFor = null;
            FailedAttempts = 0;
        }

        if (!string.IsNullOrEmpty(pin) && Verify(pin))
        {
            FailedAttempts = 0;
            Lockouts = 0;
            LockedAt = null;
            LockedFor = null;
            return Result(UnlockOutcome.Unlocked);
        }

        FailedAttempts++;

        if (FailedAttempts >= _maxAttempts)
        {
            LockedFor = CooldownAfter(Lockouts);
            LockedAt = now;
            Lockouts++;
            return Result(UnlockOutcome.LockedOut, LockedFor);
        }

        return Result(UnlockOutcome.WrongPin);
    }

    private UnlockResult Result(UnlockOutcome outcome, TimeSpan? retryAfter = null)
        => new(outcome, FailedAttempts, retryAfter, Math.Max(0, _maxAttempts - FailedAttempts));

    private bool Verify(string pin)
    {
        var salt = Convert.FromBase64String(_credential.SaltBase64);
        var expected = Convert.FromBase64String(_credential.HashBase64);
        var actual = Derive(pin, salt, _credential.Iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
}
