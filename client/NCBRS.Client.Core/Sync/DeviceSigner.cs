using System.Security.Cryptography;
using NCBRS.Devices;

namespace NCBRS.Client.Sync;

/// <summary>
/// WS-B9, device side. The device's own signing key and the signature it puts
/// on every sync upload, proving the batch came from this device and not merely
/// from someone holding a valid user token.
///
/// The private key is generated on the device and never leaves it; only the
/// public half is enrolled. Signing and the centre's verification share one
/// implementation (<see cref="DeviceSignature"/> in Contracts), so the format
/// cannot drift between them. What is signed is the raw request-body bytes, so
/// the signature survives the district tier forwarding the batch verbatim.
/// </summary>
public sealed class DeviceSigner
{
    private readonly string _privateKeyPem;

    private DeviceSigner(string privateKeyPem, string publicKeyPem)
    {
        _privateKeyPem = privateKeyPem;
        PublicKeyPem = publicKeyPem;
    }

    /// <summary>The header the signature travels in.</summary>
    public static string HeaderName => DeviceSignature.HeaderName;

    /// <summary>The public half to enrol with the centre.</summary>
    public string PublicKeyPem { get; }

    /// <summary>A brand-new device identity: a fresh P-256 key pair.</summary>
    public static DeviceSigner Generate()
    {
        var (privateKeyPem, publicKeyPem) = DeviceSignature.GenerateKeyPair();
        return new DeviceSigner(privateKeyPem, publicKeyPem);
    }

    /// <summary>
    /// Restore the signer from the private key persisted in the local encrypted
    /// store (B2); the public half is derived from it.
    /// </summary>
    public static DeviceSigner FromPrivateKey(string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        return new DeviceSigner(privateKeyPem, ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>The base64 signature over the raw batch body, for the <see cref="HeaderName"/> header.</summary>
    public string Sign(ReadOnlySpan<byte> body) => DeviceSignature.Sign(_privateKeyPem, body);
}
