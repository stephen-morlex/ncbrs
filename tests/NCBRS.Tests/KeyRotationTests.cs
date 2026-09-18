using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Certificates;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Covers signing key rotation (WS-A3/A4).
///
/// The failure this exists to prevent has a date attached: the morning after
/// a rotation, every device still holding only the old key would reject
/// every certificate signed under the new one -- and every certificate signed
/// under the old key would be rejected by anything holding only the new one.
/// Genuine documents failing exactly as forgeries do, nationally, on a day
/// nobody would connect to a key change.
///
/// The rule underneath: a certificate signed under a retired key stays valid.
/// It was genuinely issued, and rotating a key says nothing about the birth
/// it certifies. Retirement is not compromise.
/// </summary>
public class KeyRotationTests
{
    private static CertificateSigner Signer(string keyId, params RetiredSigningKey[] retired)
        => new(
            Options.Create(new CertificateSigningOptions
            {
                AllowEphemeralDevelopmentKey = true,
                KeyId = keyId,
                RetiredKeys = [.. retired]
            }),
            new DevelopmentEnvironment(),
            NullLogger<CertificateSigner>.Instance);

    private const string Canonical =
        "NCBRS|v1|100001|Ayen Deng|2026-09-10|Female|fac|2026-09-14T10:00:00Z";

    // --- the rotation itself ------------------------------------------------

    /// <summary>
    /// The case with the date attached. A certificate issued under the old
    /// key must keep verifying after the Ministry rotates.
    /// </summary>
    [Fact]
    public void ACertificateSignedBeforeRotation_StillVerifiesAfterIt()
    {
        using var oldKey = Signer("ncbrs-2026");
        var (issuedBefore, _) = oldKey.Sign(Canonical);

        // Rotation: a new active key, the old one retained verify-only.
        using var rotated = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = oldKey.PublicKeyPem() });

        Assert.Equal(Canonical, rotated.Verify(issuedBefore));
    }

    [Fact]
    public void AfterRotation_NewCertificatesCarryTheNewKeyId()
    {
        using var oldKey = Signer("ncbrs-2026");
        using var rotated = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = oldKey.PublicKeyPem() });

        var (issuedAfter, _) = rotated.Sign(Canonical);

        Assert.Equal("ncbrs-2027", issuedAfter.Split('.')[1]);
        Assert.Equal(Canonical, rotated.Verify(issuedAfter));
    }

    /// <summary>
    /// Without the retired key the old certificate is indistinguishable from
    /// a forgery -- which is exactly the disaster, and why the retired key is
    /// retained rather than dropped.
    /// </summary>
    [Fact]
    public void RotatingWithoutRetainingTheOldKey_RejectsGenuineCertificates()
    {
        using var oldKey = Signer("ncbrs-2026");
        var (issuedBefore, _) = oldKey.Sign(Canonical);

        using var rotatedCarelessly = Signer("ncbrs-2027");

        Assert.Null(rotatedCarelessly.Verify(issuedBefore));
    }

    /// <summary>
    /// Reusing the outgoing id would leave no way to tell which key signed a
    /// given certificate, so it is refused at startup rather than discovered
    /// later.
    /// </summary>
    [Fact]
    public void ARotationThatReusesTheOutgoingKeyId_RefusesToStart()
    {
        using var existing = Signer("ncbrs-2026");

        var error = Assert.Throws<InvalidOperationException>(() => Signer("ncbrs-2026",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = existing.PublicKeyPem() }));

        Assert.Contains("both the active and a retired key", error.Message);
    }

    [Fact]
    public void ARetiredKeyWithNoCertificate_RefusesToStart()
        => Assert.Throws<InvalidOperationException>(
            () => Signer("ncbrs-2027", new RetiredSigningKey { KeyId = "ncbrs-2026" }));

    [Fact]
    public void ARetiredKeyWithNoId_RefusesToStart()
    {
        using var existing = Signer("ncbrs-2026");

        Assert.Throws<InvalidOperationException>(() => Signer("ncbrs-2027",
            new RetiredSigningKey { CertificatePem = existing.PublicKeyPem() }));
    }

    /// <summary>
    /// A key nobody holds is refused like any other bad signature -- no hint
    /// that the id was the problem, which would tell a forger what to change.
    /// </summary>
    [Fact]
    public void ACertificateNamingAnUnknownKey_IsRefused()
    {
        using var stranger = Signer("some-other-ministry");
        var (foreignCertificate, _) = stranger.Sign(Canonical);

        using var ours = Signer("ncbrs-2027");

        Assert.Null(ours.Verify(foreignCertificate));
    }

    /// <summary>
    /// Several rotations accumulate: a registry running for a decade will
    /// hold documents signed under every key it has ever used.
    /// </summary>
    [Fact]
    public void KeysAccumulateAcrossRotations()
    {
        using var first = Signer("ncbrs-2025");
        using var second = Signer("ncbrs-2026");

        var (fromFirst, _) = first.Sign(Canonical);
        var (fromSecond, _) = second.Sign(Canonical);

        using var current = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2025", CertificatePem = first.PublicKeyPem() },
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = second.PublicKeyPem() });

        var (fromCurrent, _) = current.Sign(Canonical);

        Assert.Equal(Canonical, current.Verify(fromFirst));
        Assert.Equal(Canonical, current.Verify(fromSecond));
        Assert.Equal(Canonical, current.Verify(fromCurrent));
    }

    // --- what the Ministry publishes ----------------------------------------

    [Fact]
    public void TheKeySet_CarriesEveryKeyAndNamesTheActiveOne()
    {
        using var old = Signer("ncbrs-2026");
        using var current = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = old.PublicKeyPem() });

        var keys = current.VerificationKeys();

        Assert.Equal(2, keys.Count);
        Assert.Equal("ncbrs-2027", Assert.Single(keys, key => key.Active).KeyId);
        Assert.Contains(keys, key => key.KeyId == "ncbrs-2026" && !key.Active);
    }

    /// <summary>
    /// A device provisioned from the published set survives the rotation
    /// without ever contacting the registry again -- which is the situation
    /// the whole offline design puts it in.
    /// </summary>
    [Fact]
    public void ADeviceProvisionedWithTheWholeSet_VerifiesBothEras()
    {
        using var old = Signer("ncbrs-2026");
        var (issuedBefore, _) = old.Sign(Canonical);

        using var current = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = old.PublicKeyPem() });

        var (issuedAfter, _) = current.Sign(Canonical);

        using var device = CertificatePayloadVerifier.ForKeys(current.VerificationKeys());

        Assert.Equal(Canonical, device.Verify(issuedBefore));
        Assert.Equal(Canonical, device.Verify(issuedAfter));
    }

    /// <summary>
    /// The pre-rotation device: provisioned with only the old key, it still
    /// verifies what it was given and correctly refuses what it cannot check.
    /// Refreshing the offline bundle is what fixes the second half.
    /// </summary>
    [Fact]
    public void ADeviceThatHasNotRefreshed_KeepsWorkingForItsOwnEra()
    {
        using var old = Signer("ncbrs-2026");
        var (issuedBefore, _) = old.Sign(Canonical);

        using var current = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = old.PublicKeyPem() });

        var (issuedAfter, _) = current.Sign(Canonical);

        using var staleDevice = CertificatePayloadVerifier.FromPem(old.PublicKeyPem(), "ncbrs-2026");

        Assert.Equal(Canonical, staleDevice.Verify(issuedBefore));
        Assert.Null(staleDevice.Verify(issuedAfter));
    }

    // --- the revocation list under rotation ---------------------------------

    private static NCBRS.Models.CertificateRevocationList SignedList(CertificateSigner signer)
    {
        var issued = DateTime.UtcNow;
        var next = issued.AddDays(7);
        var entries = RevocationListCanonical.Order([]);

        var (_, signature) = signer.Sign(
            RevocationListCanonical.Build(null, issued, next, entries));

        return new NCBRS.Models.CertificateRevocationList(
            "NCBRS", "v1", signer.KeyId, null, issued, next, entries.Count, entries, signature);
    }

    /// <summary>
    /// The list is signed by the current key, so a device that has not
    /// refreshed since the rotation cannot check it.
    ///
    /// Untrusted rather than ignored: the device cannot tell a genuine list
    /// signed by a new key from a forged one, and guessing either way is
    /// wrong. Refreshing the offline bundle resolves it, because the bundle
    /// carries the whole key set.
    /// </summary>
    [Fact]
    public void AListSignedByAKeyTheDeviceDoesNotHold_IsUntrusted()
    {
        using var current = Signer("ncbrs-2027");
        var list = SignedList(current);

        using var staleDevice = CertificatePayloadVerifier.FromPem(
            Signer("ncbrs-2026").PublicKeyPem(), "ncbrs-2026");

        var cache = RevocationListCache.Load([list], staleDevice, DateTime.UtcNow);

        Assert.Equal(RevocationCoverage.Untrusted, cache.Coverage);
    }

    [Fact]
    public void AListSignedByAKeyTheDeviceHolds_IsTrusted()
    {
        using var old = Signer("ncbrs-2026");
        using var current = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = old.PublicKeyPem() });

        var list = SignedList(current);

        using var device = CertificatePayloadVerifier.ForKeys(current.VerificationKeys());

        Assert.Equal(RevocationCoverage.Complete,
            RevocationListCache.Load([list], device, DateTime.UtcNow).Coverage);
    }

    /// <summary>
    /// A list still signed by the retired key remains checkable, so a
    /// rotation does not invalidate copies already distributed.
    /// </summary>
    [Fact]
    public void AListSignedBeforeRotation_IsStillTrustedAfterIt()
    {
        using var old = Signer("ncbrs-2026");
        var listFromBefore = SignedList(old);

        using var current = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = old.PublicKeyPem() });

        using var device = CertificatePayloadVerifier.ForKeys(current.VerificationKeys());

        Assert.Equal(RevocationCoverage.Complete,
            RevocationListCache.Load([listFromBefore], device, DateTime.UtcNow).Coverage);
    }

    // --- the verifier itself -------------------------------------------------

    /// <summary>
    /// A verifier holding nothing would refuse everything, genuine and forged
    /// alike -- indistinguishable from the system being broken, so it is
    /// refused at construction instead.
    /// </summary>
    [Fact]
    public void AVerifierWithNoKeys_IsRefused()
        => Assert.Throws<InvalidOperationException>(
            () => CertificatePayloadVerifier.ForKeys([]));

    /// <summary>
    /// The key id selects the key rather than every key being tried. Trying
    /// them all would let a signature made under one key validate a payload
    /// claiming another.
    /// </summary>
    [Fact]
    public void ASignatureIsCheckedAgainstTheKeyItNames_NotAnyKeyThatFits()
    {
        using var keyA = Signer("key-a");
        using var keyB = Signer("key-b");

        var (signedByA, _) = keyA.Sign(Canonical);

        // Relabel A's signature as if key B had made it.
        var parts = signedByA.Split('.');
        var mislabelled = string.Join('.', parts[0], "key-b", parts[2], parts[3]);

        using var both = CertificatePayloadVerifier.ForKeys(
        [
            new VerificationKey("key-a", keyA.PublicKeyPem(), Active: true),
            new VerificationKey("key-b", keyB.PublicKeyPem())
        ]);

        Assert.Equal(Canonical, both.Verify(signedByA));
        Assert.Null(both.Verify(mislabelled));
    }

    [Fact]
    public void KnowsReportsWhichKeysAreHeld()
    {
        using var old = Signer("ncbrs-2026");
        using var current = Signer("ncbrs-2027",
            new RetiredSigningKey { KeyId = "ncbrs-2026", CertificatePem = old.PublicKeyPem() });

        using var device = CertificatePayloadVerifier.ForKeys(current.VerificationKeys());

        Assert.True(device.Knows("ncbrs-2026"));
        Assert.True(device.Knows("ncbrs-2027"));
        Assert.False(device.Knows("ncbrs-2028"));
        Assert.False(device.Knows(null));
        Assert.Equal("ncbrs-2027", device.ActiveKeyId);
    }
}
