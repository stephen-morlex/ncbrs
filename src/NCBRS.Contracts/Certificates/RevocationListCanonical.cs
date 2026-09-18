using System.Text;
using NCBRS.Models;

namespace NCBRS.Certificates;

/// <summary>
/// The exact bytes a revocation list is signed over.
///
/// In Core rather than beside the service that signs, because the verifier
/// re-derives the same string and the two must never disagree. A canonical
/// form kept in two places drifts, and drift here means genuine lists
/// failing to verify in the field with nothing to show why.
///
/// Pipe-delimited and version-led for the same reason the certificate's own
/// payload is: a verifier written in another language has to reproduce these
/// bytes exactly, and JSON guarantees neither key order nor whitespace.
/// </summary>
public static class RevocationListCanonical
{
    public const string Issuer = "NCBRS";
    public const string Version = "v1";

    public static string Build(
        DateTime? since,
        DateTime issuedAtUtc,
        DateTime nextUpdateUtc,
        IReadOnlyList<RevocationEntry> entries)
    {
        var builder = new StringBuilder();

        builder.Append(Issuer).Append("-CRL|").Append(Version)
            .Append('|').Append(since is { } from ? Stamp(from) : "-")
            .Append('|').Append(Stamp(issuedAtUtc))
            .Append('|').Append(Stamp(nextUpdateUtc))
            .Append('|').Append(entries.Count);

        foreach (var entry in entries)
        {
            builder.Append('|').Append(entry.SerialHash)
                .Append(':').Append(entry.Reason)
                .Append(':').Append(Stamp(entry.RevokedAtUtc));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Sorted so the same set of revocations always produces the same bytes.
    /// A verifier re-deriving the signed form has no other way to know what
    /// order the server's query happened to return.
    /// </summary>
    public static IReadOnlyList<RevocationEntry> Order(IEnumerable<RevocationEntry> entries)
        => [.. entries
            .Select(entry => entry with { RevokedAtUtc = AsUtc(entry.RevokedAtUtc) })
            .OrderBy(entry => entry.SerialHash, StringComparer.Ordinal)];

    /// <summary>
    /// Normalises a timestamp to UTC.
    ///
    /// SQLite returns DateTimeKind.Unspecified, and ToUniversalTime() would
    /// treat that as the server's local time and shift it -- so the signed
    /// bytes would depend on where the server runs, and a verifier abroad
    /// re-deriving them would reject a genuine list. Everything is written
    /// as UTC, so an unspecified kind is labelled rather than converted.
    /// </summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string Stamp(DateTime value)
        => AsUtc(value).ToString("yyyy-MM-ddTHH:mm:ssZ");
}
