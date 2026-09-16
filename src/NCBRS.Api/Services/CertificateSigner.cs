using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using NCBRS.Certificates;

namespace NCBRS.Services;

/// <summary>
/// Signs and verifies certificate payloads with the Ministry's X.509 key.
///
/// ECDSA P-256 rather than RSA: the signature is 64 bytes instead of 256,
/// which matters because the whole signed payload has to fit in a QR code
/// printed on the certificate. Verification has to work with no network --
/// a district office checking a certificate may have none -- so the QR
/// carries the data and the signature together, and anyone with the public
/// key can check it offline.
/// </summary>
public class CertificateSigner : IDisposable
{
    private const string PayloadPrefix = "NCBRS1";

    private readonly X509Certificate2 _certificate;
    private readonly ECDsa _privateKey;
    private readonly CertificatePayloadVerifier _verifier;
    private readonly IReadOnlyList<VerificationKey> _verificationKeys;
    private readonly ILogger<CertificateSigner> _logger;

    /// <summary>The key currently signing. Retired keys still verify.</summary>
    public string KeyId { get; }

    public CertificateSigner(
        IOptions<CertificateSigningOptions> options,
        IHostEnvironment environment,
        ILogger<CertificateSigner> logger)
    {
        _logger = logger;
        var settings = options.Value;
        KeyId = settings.KeyId;

        _certificate = Load(settings, environment, logger);

        _privateKey = _certificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException(
                "The configured signing certificate has no ECDSA private key. "
                + "NCBRS signs with ECDSA P-256; an RSA certificate will not work.");

        // Verification runs through the same public-key-only code a device
        // uses, so the centre and the field cannot disagree about whether a
        // certificate holds -- and so retired keys are honoured identically
        // on both sides.
        _verificationKeys = BuildVerificationKeys(settings, _certificate, logger);
        _verifier = CertificatePayloadVerifier.ForKeys(_verificationKeys);
    }

    private static List<VerificationKey> BuildVerificationKeys(
        CertificateSigningOptions settings,
        X509Certificate2 active,
        ILogger logger)
    {
        var keys = new List<VerificationKey>
        {
            new(settings.KeyId, active.ExportCertificatePem(), Active: true)
        };

        foreach (var retired in settings.RetiredKeys)
        {
            if (string.IsNullOrWhiteSpace(retired.KeyId))
            {
                throw new InvalidOperationException("A retired signing key must have a KeyId.");
            }

            if (retired.KeyId == settings.KeyId)
            {
                throw new InvalidOperationException(
                    $"Key '{retired.KeyId}' is listed as both the active and a retired key. "
                    + "A rotation that reuses the outgoing id leaves no way to tell which key "
                    + "signed a given certificate.");
            }

            var pem = retired.CertificatePem;

            if (string.IsNullOrWhiteSpace(pem))
            {
                if (string.IsNullOrWhiteSpace(retired.CertificatePath))
                {
                    throw new InvalidOperationException(
                        $"Retired key '{retired.KeyId}' has neither CertificatePem nor CertificatePath.");
                }

                if (!File.Exists(retired.CertificatePath))
                {
                    throw new FileNotFoundException(
                        $"Retired signing certificate for '{retired.KeyId}' not found.",
                        retired.CertificatePath);
                }

                pem = File.ReadAllText(retired.CertificatePath);
            }

            keys.Add(new VerificationKey(retired.KeyId, pem));
        }

        if (keys.Count > 1)
        {
            logger.LogInformation(
                "Signing with key {KeyId}; also verifying {Count} retired key(s): {Retired}",
                settings.KeyId, keys.Count - 1,
                string.Join(", ", keys.Skip(1).Select(key => key.KeyId)));
        }

        return keys;
    }

    private static X509Certificate2 Load(
        CertificateSigningOptions settings,
        IHostEnvironment environment,
        ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(settings.PfxPath))
        {
            if (!File.Exists(settings.PfxPath))
            {
                throw new FileNotFoundException(
                    $"Certificate signing key not found at '{settings.PfxPath}'.", settings.PfxPath);
            }

            logger.LogInformation("Loaded certificate signing key from {Path}", settings.PfxPath);

            return X509CertificateLoader.LoadPkcs12FromFile(
                settings.PfxPath,
                settings.PfxPassword,
                X509KeyStorageFlags.EphemeralKeySet);
        }

        // Refused rather than defaulted: silently minting a throwaway key
        // outside development would produce birth certificates that stop
        // verifying at the next restart, with nothing to show anything was
        // wrong until someone presented one.
        if (!environment.IsDevelopment() || !settings.AllowEphemeralDevelopmentKey)
        {
            throw new InvalidOperationException(
                "No certificate signing key is configured. Set CertificateSigning:PfxPath, "
                + "or in Development set CertificateSigning:AllowEphemeralDevelopmentKey=true "
                + "to generate a throwaway key.");
        }

        logger.LogWarning(
            "Using an EPHEMERAL self-signed certificate signing key. Certificates signed now "
            + "will stop verifying when this process restarts. Development only.");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            "CN=NCBRS Development Signing Key, O=Ministry of Health",
            key,
            HashAlgorithmName.SHA256);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>
    /// Produces the QR payload: the canonical facts and the signature over
    /// them, in a compact self-contained form a scanner can verify without
    /// contacting the registry.
    /// </summary>
    public (string QrPayload, string Signature) Sign(string canonicalPayload)
    {
        var signature = _privateKey.SignData(
            Encoding.UTF8.GetBytes(canonicalPayload),
            HashAlgorithmName.SHA256);

        var encodedSignature = Base64Url(signature);

        var qr = string.Join('.',
            PayloadPrefix,
            KeyId,
            Base64Url(Encoding.UTF8.GetBytes(canonicalPayload)),
            encodedSignature);

        return (qr, encodedSignature);
    }

    /// <summary>
    /// Checks a scanned QR payload against whichever key it names -- the
    /// current one or any retired key still in the set.
    ///
    /// Returns the canonical payload it vouches for, or null when the
    /// signature does not hold. A tampered certificate, an unparseable one
    /// and one naming a key nobody here knows are all simply "not valid",
    /// with no detail that would help someone forge one.
    /// </summary>
    public string? Verify(string qrPayload) => _verifier.Verify(qrPayload);

    /// <summary>Checks a detached signature, such as a revocation list's.</summary>
    public bool VerifyDetached(string canonicalPayload, string signature, string keyId)
        => _verifier.VerifyDetached(canonicalPayload, signature, keyId);

    /// <summary>
    /// The public half of the current key, so an offline verifier can be
    /// provisioned with it once and then check certificates with no further
    /// contact.
    /// </summary>
    public string PublicKeyPem() => _certificate.ExportCertificatePem();

    /// <summary>
    /// Every key a verifier should accept: the current one plus each retired
    /// key still verifying documents in circulation. This is what the
    /// offline bundle carries, and what makes a rotation survivable for a
    /// device that has not synced since.
    /// </summary>
    public IReadOnlyList<VerificationKey> VerificationKeys() => _verificationKeys;

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }

    public void Dispose()
    {
        _verifier.Dispose();
        _privateKey.Dispose();
        _certificate.Dispose();
        GC.SuppressFinalize(this);
    }
}
