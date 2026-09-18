using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NCBRS.Certificates;

/// <summary>One public key a verifier will accept, named by the id printed in the QR.</summary>
public record VerificationKey(string KeyId, string CertificatePem, bool Active = false);

/// <summary>
/// Checks NCBRS signatures using only public keys.
///
/// The counterpart to the API's CertificateSigner, which holds the private
/// halves and must never leave the Ministry. This lives in Core because the
/// party that most needs it is a facility device or a district office
/// scanner with no connection at all -- provisioned once, then able to check
/// certificates and revocation lists on its own.
///
/// It holds a <em>set</em> of keys, not one. Signing keys have to be
/// rotatable, and a certificate signed under the previous key stays perfectly
/// valid -- it was genuinely issued, and the register has not changed its
/// mind about the birth. A verifier that accepted only the current key would
/// reject years of genuine documents the morning after a rotation.
///
/// Retirement is not compromise. A retired key keeps verifying what it
/// signed; a <em>compromised</em> key means every certificate it ever signed
/// is suspect, which is a mass revocation and a different act entirely. That
/// is not something to express by quietly dropping a key from this set,
/// because doing so would make genuine and forged certificates fail
/// identically.
/// </summary>
public sealed class CertificatePayloadVerifier : IDisposable
{
    private const string PayloadPrefix = "NCBRS1";

    private readonly Dictionary<string, (X509Certificate2 Certificate, ECDsa PublicKey)> _keys;

    /// <summary>The key currently signing. Null when the set is verify-only.</summary>
    public string? ActiveKeyId { get; }

    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    private CertificatePayloadVerifier(IEnumerable<VerificationKey> keys)
    {
        _keys = new Dictionary<string, (X509Certificate2, ECDsa)>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var certificate = X509Certificate2.CreateFromPem(key.CertificatePem);

            var publicKey = certificate.GetECDsaPublicKey()
                ?? throw new InvalidOperationException(
                    $"Key '{key.KeyId}' has no ECDSA public key. NCBRS signs with ECDSA P-256.");

            _keys[key.KeyId] = (certificate, publicKey);

            if (key.Active)
            {
                ActiveKeyId = key.KeyId;
            }
        }

        if (_keys.Count == 0)
        {
            throw new InvalidOperationException(
                "A verifier needs at least one key. With none it would refuse every certificate, "
                + "genuine and forged alike, which is indistinguishable from the system being broken.");
        }
    }

    /// <summary>Builds a verifier from one key, for a device holding a single provisioned key.</summary>
    public static CertificatePayloadVerifier FromPem(string certificatePem, string keyId)
        => new([new VerificationKey(keyId, certificatePem, Active: true)]);

    /// <summary>
    /// Builds a verifier from the whole set the Ministry publishes -- the
    /// current key plus every retired one still verifying documents in
    /// circulation.
    /// </summary>
    public static CertificatePayloadVerifier ForKeys(IEnumerable<VerificationKey> keys) => new(keys);

    public bool Knows(string? keyId) => keyId is not null && _keys.ContainsKey(keyId);

    /// <summary>
    /// Returns the canonical payload a QR vouches for, or null when it does
    /// not hold.
    ///
    /// The key id selects which key to check against, rather than every key
    /// being tried in turn. Trying them all would let a signature made under
    /// one key validate a payload claiming another, which is a small but real
    /// confusion to leave available.
    /// </summary>
    public string? Verify(string? qrPayload)
    {
        if (string.IsNullOrWhiteSpace(qrPayload))
        {
            return null;
        }

        var parts = qrPayload.Split('.');

        if (parts.Length != 4 || parts[0] != PayloadPrefix || !_keys.TryGetValue(parts[1], out var key))
        {
            return null;
        }

        try
        {
            var payloadBytes = FromBase64Url(parts[2]);
            var signature = FromBase64Url(parts[3]);

            return key.PublicKey.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256)
                ? Encoding.UTF8.GetString(payloadBytes)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks a signature that travels beside its payload rather than inside
    /// a QR envelope -- which is how the revocation list carries its own.
    /// </summary>
    public bool VerifyDetached(string canonicalPayload, string signature, string keyId)
    {
        if (!_keys.TryGetValue(keyId, out var key))
        {
            return false;
        }

        try
        {
            return key.PublicKey.VerifyData(
                Encoding.UTF8.GetBytes(canonicalPayload),
                FromBase64Url(signature),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }

    public void Dispose()
    {
        foreach (var (certificate, publicKey) in _keys.Values)
        {
            publicKey.Dispose();
            certificate.Dispose();
        }

        _keys.Clear();
    }
}
