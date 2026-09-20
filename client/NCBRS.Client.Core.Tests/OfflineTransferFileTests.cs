using System.Text;
using NCBRS.Client.Sync;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The signed offline transfer file (WS-H2): a batch carried on removable media
/// is accepted at the sync point only if it is intact and genuinely from the
/// device it claims — a tampered file is rejected.
/// </summary>
public class OfflineTransferFileTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"deviceId":"TABLET-9","facilityId":"...","records":[{"brn":"100000"}]}""");

    [Fact]
    public void APackedFileOpensWithTheBodyIntact()
    {
        var signer = DeviceSigner.Generate();

        var file = OfflineTransferFile.Pack("TABLET-9", Body, signer);
        var result = OfflineTransferFile.Open(file, signer.PublicKeyPem);

        Assert.True(result.Accepted);
        Assert.Equal("TABLET-9", result.DeviceId);
        Assert.Equal(Body, result.Body);
    }

    [Fact]
    public void ATamperedBodyIsRejected()
    {
        var signer = DeviceSigner.Generate();
        var file = OfflineTransferFile.Pack("TABLET-9", Body, signer);

        // Flip the body inside the envelope to a different, validly-encoded
        // payload; the signature no longer matches.
        var text = Encoding.UTF8.GetString(file);
        var original = Convert.ToBase64String(Body);
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"records":[{"brn":"999999"}]}"""));
        var tampered = Encoding.UTF8.GetBytes(text.Replace(original, forged));

        var result = OfflineTransferFile.Open(tampered, signer.PublicKeyPem);

        Assert.False(result.Accepted);
        Assert.Null(result.Body);
    }

    [Fact]
    public void AFileFromAnotherDeviceDoesNotVerify()
    {
        var signer = DeviceSigner.Generate();
        var other = DeviceSigner.Generate();
        var file = OfflineTransferFile.Pack("TABLET-9", Body, signer);

        Assert.False(OfflineTransferFile.Open(file, other.PublicKeyPem).Accepted);
    }

    [Fact]
    public void GarbageAndWrongVersionAreRefusedNotCrashed()
    {
        var signer = DeviceSigner.Generate();

        var garbage = OfflineTransferFile.Open(Encoding.UTF8.GetBytes("not a transfer file"), signer.PublicKeyPem);
        Assert.False(garbage.Accepted);
        Assert.NotNull(garbage.Reason);

        var wrongVersion = Encoding.UTF8.GetBytes(
            """{"version":"something-else","deviceId":"TABLET-9","bodyBase64":"","signature":""}""");
        Assert.False(OfflineTransferFile.Open(wrongVersion, signer.PublicKeyPem).Accepted);
    }
}
