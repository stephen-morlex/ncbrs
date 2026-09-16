using NCBRS.Models;

namespace NCBRS.Certificates;

/// <summary>
/// Why a cached list can or cannot answer whether a certificate is revoked.
/// </summary>
public enum RevocationCoverage
{
    /// <summary>A complete, in-date, signed view. Absence proves not revoked.</summary>
    Complete,

    /// <summary>In date and signed, but with a gap -- absence proves nothing.</summary>
    Incomplete,

    /// <summary>Complete once, but past its NextUpdateUtc.</summary>
    Stale,

    /// <summary>A signature did not hold. Nothing here may be relied on.</summary>
    Untrusted,

    /// <summary>Nothing cached yet.</summary>
    Empty
}

/// <summary>
/// A device's cached view of the revocation list.
///
/// The hard part is not membership, it is knowing when the cache is not
/// entitled to answer. An offline verifier that treats "not in my list" as
/// "valid" has re-created the exact hole the list exists to close -- so
/// every way the cache can be short of a complete, in-date, signed view is
/// a distinct state here, and all of them refuse rather than approve.
/// </summary>
public sealed class RevocationListCache
{
    private readonly HashSet<string> _revoked;
    private readonly Dictionary<string, RevocationEntry> _entries;

    public RevocationCoverage Coverage { get; }

    /// <summary>When this view stops being trustworthy. Null when it never was.</summary>
    public DateTime? NextUpdateUtc { get; }

    public string? Detail { get; }

    public int Count => _entries.Count;

    private RevocationListCache(
        RevocationCoverage coverage,
        Dictionary<string, RevocationEntry> entries,
        DateTime? nextUpdateUtc,
        string? detail)
    {
        Coverage = coverage;
        _entries = entries;
        _revoked = [.. entries.Keys];
        NextUpdateUtc = nextUpdateUtc;
        Detail = detail;
    }

    /// <summary>
    /// Assembles a cache from what a device has downloaded: one full list
    /// and any deltas fetched since.
    ///
    /// Each list's own signature is checked, because a cache sitting on a
    /// device's disk is exactly what an attacker holding the device would
    /// edit -- dropping the entry for the certificate they intend to
    /// present. A list that does not verify makes the whole view untrusted
    /// rather than merely dropping that one list, since a tampered cache
    /// says nothing reliable about what else is missing.
    /// </summary>
    public static RevocationListCache Load(
        IEnumerable<CertificateRevocationList> lists,
        CertificatePayloadVerifier verifier,
        DateTime nowUtc)
    {
        var ordered = lists
            .OrderBy(list => RevocationListCanonical.AsUtc(list.IssuedAtUtc))
            .ToList();

        if (ordered.Count == 0)
        {
            return new RevocationListCache(RevocationCoverage.Empty, [], null,
                "No revocation list has been downloaded yet.");
        }

        foreach (var list in ordered)
        {
            if (!Signed(list, verifier))
            {
                return new RevocationListCache(RevocationCoverage.Untrusted, [], null,
                    $"A cached revocation list issued {RevocationListCanonical.AsUtc(list.IssuedAtUtc):u} "
                    + "is not signed by the Ministry key, or was altered after it was issued.");
            }
        }

        // The newest full list is the base. An older one adds nothing the
        // newer does not already contain.
        var baseList = ordered.LastOrDefault(list => list.CoversFromUtc is null);

        if (baseList is null)
        {
            // Only deltas. Each is genuine, but nothing establishes what was
            // revoked before the earliest window, so absence proves nothing.
            return new RevocationListCache(
                RevocationCoverage.Incomplete,
                Collect(ordered),
                Newest(ordered),
                "Only partial revocation lists are cached. Fetch a full list before verifying offline.");
        }

        var accepted = new List<CertificateRevocationList> { baseList };
        var coveredTo = RevocationListCanonical.AsUtc(baseList.IssuedAtUtc);

        foreach (var delta in ordered.Where(list => list.CoversFromUtc is not null
                                                    && RevocationListCanonical.AsUtc(list.IssuedAtUtc) > coveredTo)
                                     .OrderBy(list => RevocationListCanonical.AsUtc(list.IssuedAtUtc)))
        {
            var from = RevocationListCanonical.AsUtc(delta.CoversFromUtc!.Value);

            // A delta starting after the last thing we hold leaves a window
            // nothing accounts for. Anything revoked inside it is missing,
            // so this cache must stop claiming to be complete.
            if (from > coveredTo)
            {
                return new RevocationListCache(
                    RevocationCoverage.Incomplete,
                    Collect(accepted),
                    Newest(accepted),
                    $"Revocations between {coveredTo:u} and {from:u} were never downloaded. "
                    + "Fetch a full list before verifying offline.");
            }

            accepted.Add(delta);
            coveredTo = RevocationListCanonical.AsUtc(delta.IssuedAtUtc);
        }

        var nextUpdate = Newest(accepted);
        var entries = Collect(accepted);

        if (nextUpdate <= nowUtc)
        {
            return new RevocationListCache(
                RevocationCoverage.Stale, entries, nextUpdate,
                $"The cached revocation list expired {nextUpdate:u}. "
                + "Certificates cannot be confirmed until it is refreshed.");
        }

        return new RevocationListCache(RevocationCoverage.Complete, entries, nextUpdate, null);
    }

    public bool IsRevoked(string serialHash) => _revoked.Contains(serialHash);

    public RevocationEntry? Find(string serialHash)
        => _entries.TryGetValue(serialHash, out var entry) ? entry : null;

    private static bool Signed(CertificateRevocationList list, CertificatePayloadVerifier verifier)
    {
        // A list signed under a key this device does not hold is untrusted,
        // not merely unrecognised: the device cannot tell a genuine list
        // signed by a newly rotated key from a forged one, and must not
        // assume either way. Refreshing the offline bundle is what resolves
        // it, because the bundle carries the whole key set.
        if (!verifier.Knows(list.KeyId))
        {
            return false;
        }

        var canonical = RevocationListCanonical.Build(
            list.CoversFromUtc,
            list.IssuedAtUtc,
            list.NextUpdateUtc,
            RevocationListCanonical.Order(list.Entries));

        return verifier.VerifyDetached(canonical, list.Signature, list.KeyId);
    }

    private static Dictionary<string, RevocationEntry> Collect(IEnumerable<CertificateRevocationList> lists)
    {
        var entries = new Dictionary<string, RevocationEntry>(StringComparer.Ordinal);

        foreach (var entry in lists.SelectMany(list => list.Entries))
        {
            // A serial can legitimately appear in both a full list and a
            // later delta; keeping the first is enough, since a revocation
            // is never withdrawn.
            entries.TryAdd(entry.SerialHash, entry with
            {
                RevokedAtUtc = RevocationListCanonical.AsUtc(entry.RevokedAtUtc)
            });
        }

        return entries;
    }

    private static DateTime Newest(IEnumerable<CertificateRevocationList> lists)
        => lists.Max(list => RevocationListCanonical.AsUtc(list.NextUpdateUtc));
}
