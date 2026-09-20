using NCBRS.Certificates;
using NCBRS.Models;

namespace NCBRS.Client.Certificates;

/// <summary>
/// WS-B8. The device's held copy of what it needs to verify a certificate with
/// no network: the Ministry signing-key set and the cached revocation lists,
/// fetched from <c>/offline-bundle</c> and <c>/api/certificates/revocations</c>
/// whenever the device last had connectivity.
///
/// The verification itself is the centre's code, reused unchanged
/// (<see cref="OfflineCertificateVerifier"/> over <see cref="RevocationListCache"/>),
/// so a device can never reach a different verdict than the registry would. What
/// belongs to the device — and lives here — is the <em>lifecycle</em>: holding a
/// bundle, verifying against it, and knowing when it must be refreshed.
///
/// The refresh is the point of B8. A cache past its <c>NextUpdateUtc</c> stops
/// being allowed to answer "valid" and correctly but uselessly answers Unknown
/// to everything; <see cref="RefreshDue"/> is what the client checks each
/// connectivity window so it refetches before that happens. Holding the whole
/// key set is also what lets a list signed under a freshly rotated key verify
/// rather than read as forged.
///
/// The whole verifier set builds one X509 certificate per key, so a bundle owns
/// disposable handles — it is <see cref="IDisposable"/>, and a refresh disposes
/// the old bundle and constructs a new one.
/// </summary>
public sealed class CachedVerificationBundle : IDisposable
{
    private readonly IReadOnlyList<VerificationKey> _keys;
    private readonly IReadOnlyList<CertificateRevocationList> _lists;
    private CertificatePayloadVerifier? _verifier;

    private CachedVerificationBundle(
        IReadOnlyList<VerificationKey> keys,
        IReadOnlyList<CertificateRevocationList> lists,
        DateTime? fetchedAtUtc)
    {
        _keys = keys;
        _lists = lists;
        FetchedAtUtc = fetchedAtUtc;
    }

    /// <summary>Before the device has ever fetched a bundle. Verifies nothing; a refresh is always due.</summary>
    public static CachedVerificationBundle Empty { get; } = new([], [], null);

    /// <summary>
    /// A bundle as just fetched: the full signing-key set and the revocation
    /// lists the device downloaded, stamped with when. Persisted to the local
    /// encrypted store (B2) so it survives offline across restarts.
    /// </summary>
    public static CachedVerificationBundle From(
        IEnumerable<VerificationKey> signingKeys,
        IEnumerable<CertificateRevocationList> revocationLists,
        DateTime fetchedAtUtc)
        => new([.. signingKeys], [.. revocationLists], fetchedAtUtc);

    /// <summary>When the bundle was last fetched, or null if never.</summary>
    public DateTime? FetchedAtUtc { get; }

    public bool HasBundle => _keys.Count > 0;

    /// <summary>When the cached revocation view stops being trustworthy, or null if nothing is cached.</summary>
    public DateTime? ExpiresAtUtc
        => _lists.Count == 0 ? null : _lists.Max(list => RevocationListCanonical.AsUtc(list.NextUpdateUtc));

    /// <summary>
    /// Verify a scanned QR payload against the held bundle as of <paramref name="nowUtc"/>.
    /// With no bundle the answer is Unknown — a device that has never fetched
    /// the keys is in no position to accept anything.
    /// </summary>
    public OfflineVerification Verify(string? qrPayload, DateTime nowUtc)
    {
        if (!HasBundle)
        {
            return new OfflineVerification(
                OfflineVerdict.Unknown, null, null, null, null, null, null, null,
                "No signing bundle has been downloaded on this device yet. Connect to a network to "
                + "fetch it before verifying offline.");
        }

        // The verifier (keys) is stable for the life of the bundle; the cache
        // depends on the clock — its staleness is measured against now — so it
        // is rebuilt per call.
        var cache = RevocationListCache.Load(_lists, Verifier(), nowUtc);
        return new OfflineCertificateVerifier(Verifier(), cache).Verify(qrPayload);
    }

    /// <summary>
    /// Whether the device should refetch the bundle now. True when it has never
    /// fetched one, when it holds keys but no list to check against, or when the
    /// cache is within <paramref name="lead"/> of expiring — so the refetch
    /// happens before the cache goes stale and starts answering Unknown.
    /// </summary>
    public bool RefreshDue(DateTime nowUtc, TimeSpan lead = default)
    {
        if (!HasBundle || _lists.Count == 0)
        {
            return true;
        }

        var expires = _lists.Max(list => RevocationListCanonical.AsUtc(list.NextUpdateUtc));
        return nowUtc >= expires - lead;
    }

    private CertificatePayloadVerifier Verifier() => _verifier ??= CertificatePayloadVerifier.ForKeys(_keys);

    public void Dispose() => _verifier?.Dispose();
}
