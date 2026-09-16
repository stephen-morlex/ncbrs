using System.Diagnostics;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers the offline device PIN (draft Section 6.7).
///
/// A PIN is low-entropy by definition, so these pin the two things that
/// actually carry weight: the policy that removes the guesses an attacker
/// tries first, and the cost per guess.
/// </summary>
public class DevicePinHasherTests
{
    private static readonly DevicePinHasher Hasher = new();

    [Fact]
    public void ACorrectPin_Verifies()
    {
        var hash = Hasher.Hash("482913");
        Assert.True(Hasher.Verify("482913", hash));
    }

    [Fact]
    public void AWrongPin_DoesNotVerify()
    {
        var hash = Hasher.Hash("482913");
        Assert.False(Hasher.Verify("482914", hash));
    }

    /// <summary>
    /// A random salt per registrar means two people choosing the same PIN
    /// produce different hashes, so cracking one does not crack the other --
    /// and a precomputed table is useless against all of them.
    /// </summary>
    [Fact]
    public void TheSamePin_HashesDifferentlyEachTime()
    {
        var first = Hasher.Hash("482913");
        var second = Hasher.Hash("482913");

        Assert.NotEqual(first, second);
        Assert.True(Hasher.Verify("482913", first));
        Assert.True(Hasher.Verify("482913", second));
    }

    /// <summary>
    /// Cost travels with the hash, so it can be raised later without
    /// invalidating PINs already in the field.
    /// </summary>
    [Fact]
    public void AHashSetUnderOlderParameters_StillVerifies()
    {
        var cheaper = Hasher.Hash("482913", iterations: 1_000);

        Assert.Contains("$1000$", cheaper);
        Assert.True(Hasher.Verify("482913", cheaper));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("bcrypt$210000$c2FsdA==$aGFzaA==")]
    public void AMalformedStoredHash_FailsClosed(string? stored)
        => Assert.False(Hasher.Verify("482913", stored));

    [Fact]
    public void NoPinSet_MeansNoOfflineAccess()
        => Assert.False(Hasher.Verify("482913", null));

    // --- policy ---------------------------------------------------------

    [Theory]
    [InlineData("482913")]
    [InlineData("907461")]
    [InlineData("58204163")]
    public void AReasonablePin_IsAccepted(string pin)
        => Assert.True(Hasher.CheckPolicy(pin).Acceptable);

    [Theory]
    [InlineData("000000")]
    [InlineData("111111")]
    [InlineData("999999")]
    public void ARepeatedDigitPin_IsRejected(string pin)
        => Assert.False(Hasher.CheckPolicy(pin).Acceptable);

    [Theory]
    [InlineData("123456")]
    [InlineData("654321")]
    [InlineData("012345")]
    public void ASequentialPin_IsRejected(string pin)
        => Assert.False(Hasher.CheckPolicy(pin).Acceptable);

    [Theory]
    [InlineData("1234")]
    [InlineData("48291")]
    public void APinShorterThanTheMinimum_IsRejected(string pin)
        => Assert.False(Hasher.CheckPolicy(pin).Acceptable);

    [Theory]
    [InlineData("abc123")]
    [InlineData("4829 13")]
    public void ANonNumericPin_IsRejected(string pin)
        => Assert.False(Hasher.CheckPolicy(pin).Acceptable);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPin_IsRejected(string? pin)
        => Assert.False(Hasher.CheckPolicy(pin).Acceptable);

    [Fact]
    public void RejectionsExplainThemselves_SoAUserCanFixThePin()
    {
        var result = Hasher.CheckPolicy("123456");

        Assert.False(result.Acceptable);
        Assert.Contains("consecutive", result.Reason);
    }

    /// <summary>
    /// The cost per guess is the whole defence for a six-digit secret. If
    /// someone lowers the iteration count this should fail rather than
    /// quietly making every PIN in the country cheap to crack.
    /// </summary>
    [Fact]
    public void HashingIsDeliberatelyExpensive()
    {
        var stopwatch = Stopwatch.StartNew();
        Hasher.Hash("482913");
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds > 20,
            $"Hashing took only {stopwatch.ElapsedMilliseconds}ms -- too cheap to slow a brute force.");
    }

    [Fact]
    public void TheStoredHash_NeverContainsThePin()
    {
        var hash = Hasher.Hash("482913");
        Assert.DoesNotContain("482913", hash);
    }
}
