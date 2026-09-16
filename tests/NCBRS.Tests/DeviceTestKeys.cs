using System.Security.Cryptography;
using System.Text;

namespace NCBRS.Tests;

/// <summary>
/// A throwaway device key pair for the tests.
///
/// Generated per test run rather than checked in. A private key in the
/// repository is a private key in every fork of it, and the one habit worth
/// keeping is that device private keys never exist anywhere but the device.
/// </summary>
public static class DeviceTestKeys
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string PublicKeyPem { get; } = Key.ExportSubjectPublicKeyInfoPem();

    /// <summary>A second key, for "signed by the wrong device" cases.</summary>
    private static readonly ECDsa OtherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string OtherPublicKeyPem { get; } = OtherKey.ExportSubjectPublicKeyInfoPem();

    public static string Sign(string body) => Sign(Encoding.UTF8.GetBytes(body));

    public static string Sign(byte[] body)
        => Convert.ToBase64String(Key.SignData(
            body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static string SignWithOtherKey(string body)
        => Convert.ToBase64String(OtherKey.SignData(
            Encoding.UTF8.GetBytes(body), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>A P-521 key, to check the curve requirement is real.</summary>
    public static string WrongCurvePublicKeyPem { get; } =
        ECDsa.Create(ECCurve.NamedCurves.nistP521).ExportSubjectPublicKeyInfoPem();

    /// <summary>A private key PEM, to check enrolment refuses one.</summary>
    public static string PrivateKeyPem { get; } =
        ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();
}
