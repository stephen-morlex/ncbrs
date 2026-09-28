using NCBRS.Client.Auth;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The tablet checks a PIN against the centre's rules before sending it. The
/// same rule stated twice drifts unless something holds them together: this
/// runs the centre's real policy and the tablet's over the same PINs and
/// requires the same verdict, and the same reason.
/// </summary>
public class PinPolicyParityTests
{
    [Theory]
    [InlineData("246813")]      // fine
    [InlineData("975310")]      // fine
    [InlineData("135792468024")] // twelve, fine
    [InlineData("12345")]       // too short
    [InlineData("1357924680246")] // thirteen, too long
    [InlineData("123456")]      // ascending run
    [InlineData("987654")]      // descending run
    [InlineData("111111")]      // one digit repeated
    [InlineData("24681a")]      // not digits
    [InlineData("")]            // empty
    [InlineData("   ")]         // blank
    [InlineData("246 813")]     // a space
    public void TheTabletAndTheCentreAgree(string pin)
    {
        var centre = new DevicePinHasher().CheckPolicy(pin);

        Assert.Equal(centre.Acceptable ? null : centre.Reason, PinPolicy.Problem(pin));
    }

    [Fact]
    public void TheLengthsAreTheCentres()
    {
        Assert.Equal(DevicePinHasher.MinimumLength, PinPolicy.MinimumLength);
        Assert.Equal(DevicePinHasher.MaximumLength, PinPolicy.MaximumLength);
    }

    /// <summary>A PIN typed on an Arabic keypad is the same PIN, and unlocks with a hash the centre made from ASCII.</summary>
    [Fact]
    public void ArabicIndicDigitsAreTheSamePin()
    {
        var typed = PinPolicy.Normalise("٢٤٦٨١٣");
        var credential = PinCredential.FromServerHash(new DevicePinHasher().Hash("246813", iterations: 1_000))!;

        Assert.Equal("246813", typed);
        Assert.True(new OfflinePinLock(credential).Unlock(typed, DateTime.UtcNow).Unlocked);
    }
}
