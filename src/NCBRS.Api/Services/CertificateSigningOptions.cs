namespace NCBRS.Services;

/// <summary>
/// Where the Ministry's certificate signing key comes from.
///
/// This key is the most sensitive secret in the system: whoever holds it can
/// mint birth certificates that verify as genuine. It is therefore never
/// generated into the repository, never committed, and never silently
/// invented outside development.
/// </summary>
public class CertificateSigningOptions
{
    public const string SectionName = "CertificateSigning";

    /// <summary>
    /// Path to a PKCS#12 (.pfx/.p12) file holding the signing certificate
    /// and its private key. In production this should come from a secret
    /// store or HSM-backed certificate, not a file on disk.
    /// </summary>
    public string? PfxPath { get; set; }

    /// <summary>
    /// Password for the PKCS#12 file. Supply via environment variable or a
    /// secret manager -- never in appsettings.json.
    /// </summary>
    public string? PfxPassword { get; set; }

    /// <summary>
    /// Allows a throwaway self-signed key to be generated at startup when no
    /// PfxPath is configured. Development only, and the service refuses to
    /// honour it outside Development.
    ///
    /// Certificates signed by an ephemeral key stop verifying as soon as the
    /// process restarts, which is exactly why it must never reach an
    /// environment where anyone relies on a printed certificate.
    /// </summary>
    public bool AllowEphemeralDevelopmentKey { get; set; }

    /// <summary>Identifies which key signed a certificate, so keys can be rotated.</summary>
    public string KeyId { get; set; } = "ncbrs-dev";

    /// <summary>
    /// Keys that no longer sign but must still verify.
    ///
    /// A certificate signed under a previous key stays valid -- it was
    /// genuinely issued, and rotating the key says nothing about the birth.
    /// Dropping a retired key would reject years of genuine documents the
    /// morning after a rotation.
    ///
    /// Public certificates only. The private half of a retired key has no
    /// business sitting on an API server: it can still mint certificates
    /// that verify, and nothing here needs it.
    /// </summary>
    public List<RetiredSigningKey> RetiredKeys { get; set; } = [];
}

public class RetiredSigningKey
{
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Path to the public certificate, PEM encoded.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>The PEM inline, for deployments that carry it in configuration.</summary>
    public string? CertificatePem { get; set; }
}
