using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// The per-request transaction identity. Established once by
/// RequestAuditMiddleware from the caller's headers, then read back by
/// MetaEnvelopeFilter when it builds the response envelope and by
/// controllers stamping AuditLog rows -- so a request, its response, and
/// every domain write it caused all carry the same id.
/// </summary>
public sealed record TransactionContext(Guid TransactionId, string? ClientId, bool WasGenerated)
{
    public const string TransactionIdHeader = "X-Transaction-Id";
    public const string ClientIdHeader = "X-Client-Id";

    private const string ItemKey = "NCBRS.TransactionContext";

    public static void Set(HttpContext http, TransactionContext context)
        => http.Items[ItemKey] = context;

    public static TransactionContext? Get(HttpContext http)
        => http.Items.TryGetValue(ItemKey, out var value) ? value as TransactionContext : null;

    /// <summary>
    /// Takes the caller's transaction id when it supplied a usable one, and
    /// mints a UUIDv7 otherwise. A malformed header is treated as absent
    /// rather than rejected: refusing a birth registration over a bad
    /// tracking header would trade a real record for a cosmetic one.
    /// </summary>
    public static TransactionContext FromRequest(HttpRequest request)
    {
        var clientId = request.Headers[ClientIdHeader].ToString();

        if (Guid.TryParse(request.Headers[TransactionIdHeader].ToString(), out var supplied)
            && supplied != Guid.Empty)
        {
            return new TransactionContext(supplied, NullIfBlank(clientId), WasGenerated: false);
        }

        return new TransactionContext(Guid.CreateVersion7(), NullIfBlank(clientId), WasGenerated: true);
    }

    /// <summary>
    /// Folds in the meta from a request body, which wins over the headers:
    /// a value the caller put in the payload is more deliberate than one a
    /// proxy or SDK may have attached. Absent fields leave the header-derived
    /// values alone.
    /// </summary>
    public TransactionContext MergeFrom(RequestMeta meta)
    {
        var hasSuppliedId = meta.TransactionId is { } supplied && supplied != Guid.Empty;

        return new TransactionContext(
            hasSuppliedId ? meta.TransactionId!.Value : TransactionId,
            NullIfBlank(meta.ClientId ?? string.Empty) ?? ClientId,
            hasSuppliedId ? false : WasGenerated);
    }

    /// <summary>
    /// Stamps the tracking headers onto the response. Safe to call more than
    /// once while the response hasn't started: the middleware seeds them
    /// early so non-MVC responses carry an id, and MetaEnvelopeFilter
    /// restamps them once a body-supplied id has been folded in.
    /// </summary>
    public void WriteResponseHeaders(HttpResponse response)
    {
        if (response.HasStarted)
        {
            return;
        }

        response.Headers[TransactionIdHeader] = TransactionId.ToString();

        if (ClientId is not null)
        {
            response.Headers[ClientIdHeader] = ClientId;
        }
    }

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
