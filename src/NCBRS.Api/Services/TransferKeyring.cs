using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NCBRS.Devices;

namespace NCBRS.Services;

/// <summary>
/// The registry's transfer key: what tablets seal a USB transfer file to, so
/// only the registry can read the births inside (<see cref="SealedTransfer"/>).
///
/// Separate from the certificate signing key on purpose. That key proves
/// documents are genuine and is published so anyone can verify; this one
/// decrypts personal data and is never published. One key doing both would
/// tie the rotation of a signature everyone relies on to the rotation of a
/// secret that must change whenever it may have leaked.
/// </summary>
public class TransferEncryptionOptions
{
    public const string SectionName = "TransferEncryption";

    /// <summary>The default id, and the throwaway development key's. Refused outside Development.</summary>
    public const string DevelopmentKeyId = "ncbrs-transfer-dev";

    /// <summary>Named in every sealed file, so the registry knows which key opens it.</summary>
    public string KeyId { get; set; } = DevelopmentKeyId;

    /// <summary>Path to the private key, PKCS#8 PEM. From a secret store in production, never the repository.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// A throwaway key generated at startup when no key is configured.
    /// Development only, and refused anywhere else: files sealed to it cannot
    /// be opened once the process restarts, so a stick carried across a
    /// restart would hold births nobody can read.
    /// </summary>
    public bool AllowEphemeralDevelopmentKey { get; set; }

    /// <summary>
    /// Keys files may still be sealed to. A tablet seals to the key in the
    /// bundle it last fetched, and a stick may take weeks to arrive, so after a
    /// rotation the outgoing private key must still open what was sealed to it.
    /// Unlike a retired signing key, the private half is needed here.
    /// </summary>
    public List<RetiredTransferKey> RetiredKeys { get; set; } = [];
}

public class RetiredTransferKey
{
    public string KeyId { get; set; } = string.Empty;

    public string? PrivateKeyPath { get; set; }
}

/// <summary>Holds the transfer keys for the process; see <see cref="TransferEncryptionOptions"/>.</summary>
public sealed class TransferKeyring : IDisposable
{
    private readonly Dictionary<string, ECDiffieHellman> _keys = new(StringComparer.Ordinal);

    public TransferKeyring(IOptions<TransferEncryptionOptions> options, IHostEnvironment environment, ILogger<TransferKeyring> logger)
    {
        var settings = options.Value;
        KeyId = settings.KeyId;

        if (!environment.IsDevelopment() && KeyId == TransferEncryptionOptions.DevelopmentKeyId)
        {
            throw new InvalidOperationException(
                $"TransferEncryption:KeyId is the development default '{TransferEncryptionOptions.DevelopmentKeyId}' "
                + "outside Development. It is named in every sealed transfer file; set a production-unique id.");
        }

        _keys[KeyId] = LoadActive(settings, environment, logger);

        foreach (var retired in settings.RetiredKeys)
        {
            if (string.IsNullOrWhiteSpace(retired.KeyId) || retired.KeyId == KeyId || _keys.ContainsKey(retired.KeyId))
            {
                throw new InvalidOperationException(
                    $"Retired transfer key '{retired.KeyId}' needs a KeyId of its own, distinct from the active key and every other.");
            }

            _keys[retired.KeyId] = Load(retired.PrivateKeyPath, $"retired transfer key '{retired.KeyId}'");
        }

        PublicKeyPem = _keys[KeyId].ExportSubjectPublicKeyInfoPem();
    }

    public string KeyId { get; }

    /// <summary>What tablets seal to, published in the offline bundle.</summary>
    public string PublicKeyPem { get; }

    public SealedTransferResult Open(ReadOnlySpan<byte> sealedFile)
        => SealedTransfer.Open(sealedFile, keyId => _keys.GetValueOrDefault(keyId));

    public SealedTransferResult Open(SealedTransferFile sealedFile)
        => SealedTransfer.Open(sealedFile, keyId => _keys.GetValueOrDefault(keyId));

    private static ECDiffieHellman LoadActive(TransferEncryptionOptions settings, IHostEnvironment environment, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(settings.PrivateKeyPath))
        {
            return Load(settings.PrivateKeyPath, "the transfer key");
        }

        if (settings.AllowEphemeralDevelopmentKey && environment.IsDevelopment())
        {
            logger.LogWarning(
                "No TransferEncryption:PrivateKeyPath configured; using a throwaway development key. "
                + "Transfer files sealed to it cannot be opened after this process restarts.");
            return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        }

        throw new InvalidOperationException(
            "TransferEncryption:PrivateKeyPath must name the registry's transfer key (PKCS#8 PEM, P-256). "
            + "Without it, tablets have nothing to seal USB transfer files to, and a sealed file could never be opened.");
    }

    private static ECDiffieHellman Load(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"The private key for {what} was not found at '{path}'.");
        }

        var key = ECDiffieHellman.Create();
        key.ImportFromPem(File.ReadAllText(path));
        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new InvalidOperationException($"{what} must be a P-256 key.");
        }

        return key;
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }
    }
}
