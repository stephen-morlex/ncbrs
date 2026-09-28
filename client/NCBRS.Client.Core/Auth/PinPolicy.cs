namespace NCBRS.Client.Auth;

/// <summary>
/// The centre's PIN rules, checked on the tablet before a PIN is sent, so a
/// registrar learns what is wrong while typing rather than after a round trip
/// that may not be possible. The centre still decides: this is the same rule
/// stated twice, and <c>PinPolicyParityTests</c> holds the two to the same
/// answers, so a change at the centre that is not made here fails a test.
/// </summary>
public static class PinPolicy
{
    public const int MinimumLength = 6;
    public const int MaximumLength = 12;

    /// <summary>
    /// A PIN as the tablet sends and checks it: every decimal digit as its ASCII
    /// digit. A numeric keypad in Arabic gives Arabic-Indic digits, which hash
    /// to something else entirely, so the same PIN typed on two keyboards would
    /// otherwise be two PINs.
    /// </summary>
    public static string Normalise(string? pin)
        => new((pin ?? "").Trim().Select(c => char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c).ToArray());

    /// <summary>Null when acceptable, else the reason, in the centre's words.</summary>
    public static string? Problem(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return "A PIN is required.";
        }

        if (pin.Length < MinimumLength || pin.Length > MaximumLength)
        {
            return $"A PIN must be between {MinimumLength} and {MaximumLength} characters.";
        }

        if (!pin.All(char.IsDigit))
        {
            return "A PIN must contain digits only.";
        }

        if (pin.Distinct().Count() == 1)
        {
            return "A PIN cannot be a single repeated digit.";
        }

        var ascending = true;
        var descending = true;
        for (var i = 1; i < pin.Length; i++)
        {
            var step = pin[i] - pin[i - 1];
            ascending &= step == 1;
            descending &= step == -1;
        }

        return ascending || descending ? "A PIN cannot be a run of consecutive digits." : null;
    }
}
