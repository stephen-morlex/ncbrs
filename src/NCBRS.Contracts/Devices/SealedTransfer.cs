using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NCBRS.Devices;

/// <summary>The on-disk shape of a sealed transfer file. Everything but the ciphertext is public.</summary>
public sealed record SealedTransferFile(
    string Version,
    string KeyId,
    string EphemeralKey,
    string Nonce,
    string Ciphertext,
    string Tag);

/// <summary>What opening a sealed transfer file produced.</summary>
public sealed record SealedTransferResult(bool Opened, byte[]? Plaintext, string? KeyId, string? Reason)
{
    public static SealedTransferResult Refuse(string reason, string? keyId = null) => new(false, null, keyId, reason);
}

/// <summary>
/// A transfer file sealed to the registry, so births carried on a USB stick
/// cannot be read by anyone who finds, copies or carries it — only by the
/// registry, which holds the private key.
///
/// A signed transfer envelope (<see cref="TransferEnvelopes"/>) gives
/// integrity: a changed file is refused. It gave no confidentiality: the
/// children's and parents' names and dates of birth sat in it as base64, on
/// media that passes through several hands between a post and a district
/// office. Sealing adds that. Signed first, then sealed: the signature stays
/// over the batch's own bytes, so once the registry has opened the file the
/// check is the one every upload goes through.
///
/// The construction is ECIES over P-256: a fresh ephemeral key per file, ECDH
/// with the registry's public key, HKDF-SHA256 to a 256-bit key, and AES-GCM.
/// The version, key id and ephemeral key are bound in as associated data, so
/// none can be swapped without the tag failing. A fresh ephemeral key per file
/// means no two files share a key, and a device holds nothing that could open
/// a file it sealed — a stolen tablet opens no stick.
///
/// In Contracts, like <see cref="DeviceSignature"/>: the tablet seals and the
/// registry opens, and one implementation is what keeps the two in step.
/// </summary>
public static class SealedTransfer
{
    public const string CurrentVersion = "ncbrs-sealed-transfer-v1";

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A new P-256 key pair for the registry's transfer key.</summary>
    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Seal <paramref name="plaintext"/> so that only the holder of the key <paramref name="keyId"/> can read it.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> plaintext, string recipientPublicKeyPem, string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("The registry's transfer key id is required.", nameof(keyId));
        }

        using var recipient = ECDiffieHellman.Create();
        recipient.ImportFromPem(recipientPublicKeyPem);
        if (recipient.KeySize != 256)
        {
            throw new ArgumentException("The registry's transfer key must be a P-256 key.", nameof(recipientPublicKeyPem));
        }

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralKey = Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo());

        var key = DeriveKey(ephemeral.DeriveRawSecretAgreement(recipient.PublicKey), ephemeralKey);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(CurrentVersion, keyId, ephemeralKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return JsonSerializer.SerializeToUtf8Bytes(new SealedTransferFile(
            CurrentVersion, keyId, ephemeralKey,
            Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)), Json);
    }

    /// <summary>
    /// Open a sealed file. <paramref name="privateKeyFor"/> gives the private
    /// key the file names, or null if this registry holds no such key — a file
    /// sealed to a key retired long ago, or to another registry altogether.
    /// </summary>
    public static SealedTransferResult Open(ReadOnlySpan<byte> file, Func<string, ECDiffieHellman?> privateKeyFor)
    {
        SealedTransferFile? sealedFile;
        try
        {
            sealedFile = JsonSerializer.Deserialize<SealedTransferFile>(file, Json);
        }
        catch (JsonException)
        {
            return SealedTransferResult.Refuse("The file is not a sealed NCBRS transfer file.");
        }

        return sealedFile is null
            ? SealedTransferResult.Refuse("The sealed file is empty.")
            : Open(sealedFile, privateKeyFor);
    }

    /// <summary>
    /// Open a sealed file already parsed — the shape an upload carries it in.
    /// Nothing depends on the file's exact bytes: the tag covers the fields,
    /// so parsed and re-read it opens, or fails, exactly as the file would.
    /// </summary>
    public static SealedTransferResult Open(SealedTransferFile sealedFile, Func<string, ECDiffieHellman?> privateKeyFor)
    {
        if (sealedFile.Version != CurrentVersion)
        {
            return SealedTransferResult.Refuse(
                $"Unrecognised sealed transfer file version '{sealedFile?.Version}'; this reader expects '{CurrentVersion}'.");
        }

        var privateKey = privateKeyFor(sealedFile.KeyId);
        if (privateKey is null)
        {
            return SealedTransferResult.Refuse(
                $"The file is sealed to key '{sealedFile.KeyId}', which this registry does not hold.", sealedFile.KeyId);
        }

        byte[] nonce, ciphertext, tag, ephemeralSpki;
        try
        {
            nonce = Convert.FromBase64String(sealedFile.Nonce);
            ciphertext = Convert.FromBase64String(sealedFile.Ciphertext);
            tag = Convert.FromBase64String(sealedFile.Tag);
            ephemeralSpki = Convert.FromBase64String(sealedFile.EphemeralKey);
        }
        catch (FormatException)
        {
            return SealedTransferResult.Refuse("The sealed file is damaged: a field is not valid base64.", sealedFile.KeyId);
        }

        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            return SealedTransferResult.Refuse("The sealed file is damaged: its nonce or tag is the wrong size.", sealedFile.KeyId);
        }

        byte[] key;
        try
        {
            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportSubjectPublicKeyInfo(ephemeralSpki, out _);
            key = DeriveKey(privateKey.DeriveRawSecretAgreement(ephemeral.PublicKey), sealedFile.EphemeralKey);
        }
        catch (CryptographicException)
        {
            return SealedTransferResult.Refuse("The sealed file is damaged: its ephemeral key is not a valid P-256 key.", sealedFile.KeyId);
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(sealedFile.Version, sealedFile.KeyId, sealedFile.EphemeralKey));
        }
        catch (AuthenticationTagMismatchException)
        {
            return SealedTransferResult.Refuse(
                "The sealed file could not be opened: it has been altered, or was not sealed to this registry's key.", sealedFile.KeyId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new SealedTransferResult(true, plaintext, sealedFile.KeyId, null);
    }

    private static byte[] DeriveKey(byte[] sharedSecret, string ephemeralKey)
    {
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256, sharedSecret, KeySize,
                salt: Encoding.ASCII.GetBytes(ephemeralKey),
                info: Encoding.ASCII.GetBytes(CurrentVersion));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private static byte[] AssociatedData(string version, string keyId, string ephemeralKey)
        => Encoding.UTF8.GetBytes($"{version}\n{keyId}\n{ephemeralKey}");
}
