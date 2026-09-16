using System.Security.Cryptography;

namespace NCBRS.Services;

public record PinPolicyResult(bool Acceptable, string? Reason = null);

/// <summary>
/// Hashes and verifies a registrar's offline device PIN.
///
/// What this protects, and what it does not: the PIN stops someone who picks
/// up a village post's tablet from registering births with it. It is NOT a
/// server-side authentication mechanism -- the device verifies it locally
/// while offline, so the server can never know whether a PIN was actually
/// entered. That is inherent to offline-first, not a shortcoming of the
/// implementation.
///
/// A PIN is low-entropy by definition: six digits is a million guesses, which
/// any machine exhausts quickly. Hashing cannot fix that. PBKDF2 with a high
/// iteration count raises the cost per guess, and the policy below removes
/// the guesses an attacker would try first, but the defences that actually
/// matter are device-side lockout after a few failures and the ability to
/// revoke a stolen device.
/// </summary>
public class DevicePinHasher
{
    /// <summary>
    /// Deliberately expensive. It runs once per unlock on a low-end tablet,
    /// which is tolerable, and once per guess for an attacker with the
    /// device, which is the point. Stored inside each hash so this can be
    /// raised later without invalidating PINs already set.
    /// </summary>
    public const int DefaultIterations = 210_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Algorithm = "pbkdf2-sha256";

    public const int MinimumLength = 6;
    public const int MaximumLength = 12;

    /// <summary>
    /// Rejects the PINs an attacker tries first. Against a six-digit secret
    /// this is worth more than any amount of extra hashing: it removes the
    /// small set of values a large share of people actually choose.
    /// </summary>
    public PinPolicyResult CheckPolicy(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return new PinPolicyResult(false, "A PIN is required.");
        }

        if (pin.Length < MinimumLength || pin.Length > MaximumLength)
        {
            return new PinPolicyResult(false,
                $"A PIN must be between {MinimumLength} and {MaximumLength} characters.");
        }

        if (!pin.All(char.IsDigit))
        {
            return new PinPolicyResult(false, "A PIN must contain digits only.");
        }

        if (pin.Distinct().Count() == 1)
        {
            return new PinPolicyResult(false, "A PIN cannot be a single repeated digit.");
        }

        if (IsSequential(pin))
        {
            return new PinPolicyResult(false, "A PIN cannot be a run of consecutive digits.");
        }

        return new PinPolicyResult(true);
    }

    /// <summary>
    /// Returns a self-describing hash: algorithm, cost and salt travel with
    /// it, so a device can verify a PIN set under older parameters and the
    /// cost can be raised without a mass reset.
    /// </summary>
    public string Hash(string pin, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var derived = Derive(pin, salt, iterations);

        return string.Join('$',
            Algorithm,
            iterations.ToString(),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(derived));
    }

    public bool Verify(string pin, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');

        if (parts.Length != 4 || parts[0] != Algorithm || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Derive(pin, salt, iterations);

            // Constant-time: a length-or-content comparison that returns early
            // leaks how much of a guess was right.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private static bool IsSequential(string pin)
    {
        var ascending = true;
        var descending = true;

        for (var i = 1; i < pin.Length; i++)
        {
            var step = pin[i] - pin[i - 1];
            ascending &= step == 1;
            descending &= step == -1;
        }

        return ascending || descending;
    }
}
