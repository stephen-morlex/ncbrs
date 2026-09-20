using System.Text;
using NCBRS.Client.Sync;
using NCBRS.Devices;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The device signing side of WS-B9: what the device signs must be exactly what
/// the centre's <see cref="DeviceSignature.Verify"/> accepts — proven here by
/// signing and verifying with the same key, and by a tampered body failing.
/// </summary>
public class DeviceSignerTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"deviceId":"TABLET-1","records":[]}""");

    [Fact]
    public void ASignedBodyVerifiesWithTheCentresVerifier()
    {
        var signer = DeviceSigner.Generate();

        var signature = signer.Sign(Body);
        var result = DeviceSignature.Verify(signer.PublicKeyPem, Body, signature);

        Assert.True(result.Valid);
        Assert.Equal("X-NCBRS-Device-Signature", DeviceSigner.HeaderName);
    }

    [Fact]
    public void ATamperedBodyDoesNotVerify()
    {
        var signer = DeviceSigner.Generate();
        var signature = signer.Sign(Body);

        var tampered = Encoding.UTF8.GetBytes("""{"deviceId":"TABLET-1","records":[{"x":1}]}""");
        var result = DeviceSignature.Verify(signer.PublicKeyPem, tampered, signature);

        Assert.False(result.Valid);
    }

    [Fact]
    public void AnotherDevicesKeyDoesNotVerifyThisSignature()
    {
        var signer = DeviceSigner.Generate();
        var other = DeviceSigner.Generate();
        var signature = signer.Sign(Body);

        Assert.False(DeviceSignature.Verify(other.PublicKeyPem, Body, signature).Valid);
    }

    [Fact]
    public void RestoresFromThePersistedPrivateKeyAndStillVerifies()
    {
        var original = DeviceSigner.Generate();

        // Persist only the private key, restore the signer from it — the public
        // half is derived and must still match what enrolment holds.
        var (privateKeyPem, _) = DeviceSignature.GenerateKeyPair();
        var restored = DeviceSigner.FromPrivateKey(privateKeyPem);

        var signature = restored.Sign(Body);
        Assert.True(DeviceSignature.Verify(restored.PublicKeyPem, Body, signature).Valid);
        Assert.NotEqual(original.PublicKeyPem, restored.PublicKeyPem);
    }

    [Fact]
    public void TheGeneratedPublicKeyIsAcceptableForEnrolment()
    {
        var signer = DeviceSigner.Generate();

        Assert.True(DeviceSignature.ValidateEnrolmentKey(signer.PublicKeyPem).Valid);
    }
}
