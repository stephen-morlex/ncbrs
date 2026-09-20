using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NCBRS.Certificates;
using NCBRS.Client.Certificates;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The device's offline verification bundle (WS-B8): it refuses when it has no
/// bundle, delegates real verification to the centre's code, and knows when it
/// must refresh before the cache goes stale.
/// </summary>
public class CachedVerificationBundleTests
{
    private static VerificationKey SelfSignedKey(string keyId = "test-key")
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Test Ministry", ecdsa, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new VerificationKey(keyId, certificate.ExportCertificatePem(), Active: true);
    }

    private static CertificateRevocationList ListExpiring(DateTime nextUpdateUtc)
        => new(
            Issuer: "NCBRS",
            Version: "v1",
            KeyId: "test-key",
            CoversFromUtc: null,
            IssuedAtUtc: nextUpdateUtc.AddDays(-1),
            NextUpdateUtc: nextUpdateUtc,
            Count: 0,
            Entries: [],
            Signature: "unchecked-by-refreshdue");

    [Fact]
    public void WithNoBundle_VerifyIsUnknownAndRefreshIsDue()
    {
        var now = DateTime.UtcNow;

        var result = CachedVerificationBundle.Empty.Verify("anything", now);

        Assert.Equal(OfflineVerdict.Unknown, result.Verdict);
        Assert.False(result.Accept);
        Assert.False(CachedVerificationBundle.Empty.HasBundle);
        Assert.True(CachedVerificationBundle.Empty.RefreshDue(now));
    }

    [Fact]
    public void WithABundle_DelegatesToTheCentresVerifier()
    {
        var now = DateTime.UtcNow;
        using var bundle = CachedVerificationBundle.From([SelfSignedKey()], [], now);

        // A payload that is not a genuine signature is NotGenuine — the same
        // verdict the online endpoint gives — proving the delegation path runs.
        var result = bundle.Verify("not.a.real.signature", now);

        Assert.True(bundle.HasBundle);
        Assert.Equal(OfflineVerdict.NotGenuine, result.Verdict);
        Assert.Equal(now, bundle.FetchedAtUtc);
    }

    [Fact]
    public void RefreshIsDueWhenTheCacheIsExpiredOrWithinTheLead()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        using var fresh = CachedVerificationBundle.From([SelfSignedKey()], [ListExpiring(now.AddDays(7))], now);
        Assert.False(fresh.RefreshDue(now));
        Assert.Equal(now.AddDays(7), fresh.ExpiresAtUtc);

        using var expired = CachedVerificationBundle.From([SelfSignedKey()], [ListExpiring(now.AddHours(-1))], now);
        Assert.True(expired.RefreshDue(now));

        // A day-long lead: due when expiry is less than a day away.
        using var soon = CachedVerificationBundle.From([SelfSignedKey()], [ListExpiring(now.AddHours(12))], now);
        Assert.True(soon.RefreshDue(now, TimeSpan.FromDays(1)));
        Assert.False(soon.RefreshDue(now));
    }

    [Fact]
    public void RefreshIsDueWhenKeysAreHeldButNoListToCheckAgainst()
    {
        var now = DateTime.UtcNow;
        using var noLists = CachedVerificationBundle.From([SelfSignedKey()], [], now);

        // Keys but nothing to prove not-revoked against — it must fetch a list.
        Assert.True(noLists.RefreshDue(now));
        Assert.Null(noLists.ExpiresAtUtc);
    }
}
