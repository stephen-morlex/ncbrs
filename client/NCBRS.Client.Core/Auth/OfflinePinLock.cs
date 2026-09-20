using System.Security.Cryptography;
using System.Text;

namespace NCBRS.Client.Auth;

/// <summary>
/// The PIN credential provisioned to the device while it had connectivity, so
/// the registrar can be checked offline. Only the salted PBKDF2 hash is stored —
/// never the PIN — because the device this sits on is exactly what a thief holds.
/// </summary>
public sealed record PinCredential(string SaltBase64, int Iterations, string HashBase64);

public enum UnlockOutcome
{
    Unlocked,
    WrongPin,
    LockedOut
}

public sealed record UnlockResult(
    UnlockOutcome Outcome, int FailedAttempts, DateTime? LockedUntilUtc, int AttemptsRemaining)
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
/// A pure state machine: the caller persists <see cref="FailedAttempts"/> and
/// <see cref="LockedUntilUtc"/> to the encrypted store (B2) so the limit
/// survives the obvious attack of force-quitting between guesses.
/// </summary>
public sealed class OfflinePinLock
{
    private const int DefaultIterations = 100_000;
    private static readonly TimeSpan DefaultLockout = TimeSpan.FromMinutes(5);

    private readonly PinCredential _credential;
    private readonly int _maxAttempts;
    private readonly TimeSpan _lockout;

    public OfflinePinLock(
        PinCredential credential,
        int maxAttempts = 5,
        TimeSpan lockout = default,
        int failedAttempts = 0,
        DateTime? lockedUntilUtc = null)
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
        LockedUntilUtc = lockedUntilUtc;
    }

    public int FailedAttempts { get; private set; }

    public DateTime? LockedUntilUtc { get; private set; }

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

    public UnlockResult Unlock(string? pin, DateTime nowUtc)
    {
        // Locked check first: guessing must not even reach the comparison while
        // the cooldown is running, let alone reset it.
        if (LockedUntilUtc is { } until && nowUtc < until)
        {
            return Result(UnlockOutcome.LockedOut);
        }

        // A cooldown that has elapsed opens a fresh window.
        if (LockedUntilUtc is not null)
        {
            LockedUntilUtc = null;
            FailedAttempts = 0;
        }

        if (!string.IsNullOrEmpty(pin) && Verify(pin))
        {
            FailedAttempts = 0;
            LockedUntilUtc = null;
            return Result(UnlockOutcome.Unlocked);
        }

        FailedAttempts++;

        if (FailedAttempts >= _maxAttempts)
        {
            LockedUntilUtc = nowUtc + _lockout;
            return Result(UnlockOutcome.LockedOut);
        }

        return Result(UnlockOutcome.WrongPin);
    }

    private UnlockResult Result(UnlockOutcome outcome)
        => new(outcome, FailedAttempts, LockedUntilUtc, Math.Max(0, _maxAttempts - FailedAttempts));

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
