using NCBRS.Client.Auth;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// A PIN a registrar sets at the centre is what unlocks the tablet offline, so
/// the centre's hash and the tablet's lock must agree exactly. Each side is
/// tested on its own elsewhere; this pins the two together with the real code
/// on both sides — a change to either that breaks the other fails here, not in
/// a village post with nobody able to sign in.
/// </summary>
public class DevicePinCompatibilityTests
{
    private readonly DevicePinHasher _hasher = new();

    [Fact]
    public void APinHashedByTheCentreUnlocksTheTablet()
    {
        var credential = PinCredential.FromServerHash(_hasher.Hash("246813", iterations: 1_000));

        Assert.NotNull(credential);
        Assert.True(new OfflinePinLock(credential).Unlock("246813", DateTime.UtcNow).Unlocked);
    }

    [Fact]
    public void TheWrongPinDoesNotUnlock()
    {
        var credential = PinCredential.FromServerHash(_hasher.Hash("246813", iterations: 1_000))!;

        Assert.False(new OfflinePinLock(credential).Unlock("246814", DateTime.UtcNow).Unlocked);
    }

    /// <summary>The cost travels in the hash, so the tablet verifies at whatever the centre used.</summary>
    [Fact]
    public void TheCentresDefaultCostIsReadFromTheHash()
    {
        var credential = PinCredential.FromServerHash(_hasher.Hash("975310"))!;

        Assert.Equal(DevicePinHasher.DefaultIterations, credential.Iterations);
    }
}
