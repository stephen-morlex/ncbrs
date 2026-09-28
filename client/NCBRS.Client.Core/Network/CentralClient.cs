using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NCBRS.Client.Sync;
using NCBRS.Models;

namespace NCBRS.Client.Network;

/// <summary>
/// What became of a call to the centre, in the terms a device has to act on.
/// Every call answers with one of these rather than throwing, because on a
/// village link "it did not work" is the normal case and has five different
/// remedies.
/// </summary>
public enum CentralOutcome
{
    /// <summary>Done. The value is the centre's answer.</summary>
    Succeeded,

    /// <summary>
    /// A District node has the batch and will forward it; the centre has not
    /// seen it yet. <b>A family must not be told the registration is confirmed.</b>
    /// Keep the upload and send it again next window: the node answers with
    /// the centre's result once it has one.
    /// </summary>
    Held,

    /// <summary>
    /// The same transaction is already being processed (a retry overlapping
    /// the attempt it follows). Not an error: send the same upload again after
    /// <see cref="CentralResult{T}.RetryAfter"/>.
    /// </summary>
    InProgress,

    /// <summary>
    /// The centre said no, for a reason retrying the same request will not
    /// change: validation, a device not enrolled here, a failed signature. See
    /// <see cref="CentralResult{T}.Errors"/>.
    /// </summary>
    Refused,

    /// <summary>
    /// No usable sign-in: no token, or the centre did not accept it. The
    /// registrar has to sign in again while there is connectivity. Nothing is
    /// lost; keep the upload.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// The centre did not answer, or answered that it could not right now
    /// (5xx, 408, 429). Keep the upload and try again next window.
    /// </summary>
    Unreachable,
}

public sealed record CentralResult<T>(
    CentralOutcome Outcome,
    T? Value = default,
    int? StatusCode = null,
    IReadOnlyList<ApiError>? Errors = null,
    TimeSpan? RetryAfter = null,
    string? Detail = null)
{
    public bool Succeeded => Outcome == CentralOutcome.Succeeded;
}

/// <summary>
/// The signed-in registrar's access token, or null when nobody is signed in.
/// How the registrar signs in on a tablet is the shell's (and a pending
/// decision); this layer only needs the token.
/// </summary>
public delegate Task<string?> AccessTokenProvider(CancellationToken cancellationToken);

/// <summary>
/// Where the device talks to. Uploads may go through a District node
/// (<see cref="SyncVia"/>) when that is what the post can reach; everything
/// else — enrolment, BRN blocks, the verification bundle — is the centre's
/// alone, since a District node carries sync batches and nothing else.
/// </summary>
public sealed record CentralEndpoints(Uri Centre, Uri? SyncVia = null)
{
    public Uri SyncBase => SyncVia ?? Centre;
}

/// <summary>
/// The device's connection to the national tier: every call the Tier-1 client
/// makes, speaking the centre's contract exactly — the <c>{ meta, data }</c>
/// envelope, a transaction id on every write, and the device signature over
/// the exact bytes sent on every write that names the device.
///
/// It understands both answers a sync can get: the centre's own, and a District
/// node's, which is either "held" (202) or the centre's answer carried back
/// verbatim once the node has forwarded the batch.
/// </summary>
public sealed class CentralClient(HttpClient http, CentralEndpoints endpoints, AccessTokenProvider accessToken)
{
    /// <summary>The header the centre reads a caller-supplied transaction id from.</summary>
    public const string TransactionIdHeader = "X-Transaction-Id";

    /// <summary>
    /// Enrol a device key against a facility. This is the district officer's
    /// act at handover, so the token is theirs (it needs the right to enrol
    /// devices). Only the public half of the key is sent.
    /// </summary>
    public Task<CentralResult<DeviceResponse>> EnrolDeviceAsync(
        EnrolDeviceRequest request, CancellationToken cancellationToken = default)
        => SendAsync<DeviceResponse>(
            endpoints.Centre, "api/devices", Envelope(request, Guid.CreateVersion7()), signer: null, cancellationToken);

    /// <summary>
    /// Draw the next block of registration numbers for this device. Signed:
    /// a block nobody can attribute is weeks of registrations nobody can
    /// attribute either, so the centre holds this to the device's proof.
    /// </summary>
    public Task<CentralResult<BrnBlockResponse>> RequestBrnBlockAsync(
        Guid facilityId,
        string deviceId,
        DeviceSigner signer,
        int blockSize = 200,
        CancellationToken cancellationToken = default)
        => SendAsync<BrnBlockResponse>(
            endpoints.Centre,
            $"api/BirthRecords/{facilityId}/request-brn-block",
            Envelope(new BrnBlockRequest { BlockSize = blockSize, DeviceId = deviceId }, Guid.CreateVersion7()),
            signer,
            cancellationToken);

    /// <summary>
    /// Everything needed to verify certificates offline: every signing key in
    /// use and the current revocation list. Anonymous — it names nobody.
    /// </summary>
    public async Task<CentralResult<OfflineVerificationBundle>> FetchOfflineBundleAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoints.Centre, "api/certificates/offline-bundle"));
        return await ExchangeAsync<OfflineVerificationBundle>(request, cancellationToken);
    }

    /// <summary>
    /// Send a signed upload — its exact bytes, its signature, its transaction
    /// id — to the centre or through the District node. Retrying the same
    /// upload is always safe: both tiers recognise the transaction.
    /// </summary>
    public async Task<CentralResult<SyncBatchResponse>> UploadAsync(
        SignedUpload upload, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoints.SyncBase, "api/Sync/batches"))
        {
            Content = Json(upload.Body),
        };
        request.Headers.TryAddWithoutValidation(upload.HeaderName, upload.Signature);
        request.Headers.TryAddWithoutValidation(TransactionIdHeader, upload.TransactionId.ToString());

        return await AuthorisedExchangeAsync<SyncBatchResponse>(request, cancellationToken);
    }

    private static byte[] Envelope<T>(T data, Guid transactionId)
        => JsonSerializer.SerializeToUtf8Bytes(
            new ApiRequest<T> { Meta = new RequestMeta { TransactionId = transactionId }, Data = data },
            ClientJson.Options);

    private async Task<CentralResult<T>> SendAsync<T>(
        Uri root, string path, byte[] body, DeviceSigner? signer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, path)) { Content = Json(body) };

        // Serialised once and these exact bytes signed and sent: re-serialising
        // after signing changes whitespace or order and the signature fails.
        if (signer is not null)
        {
            request.Headers.TryAddWithoutValidation(DeviceSigner.HeaderName, signer.Sign(body));
        }

        return await AuthorisedExchangeAsync<T>(request, cancellationToken);
    }

    private static ByteArrayContent Json(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<CentralResult<T>> AuthorisedExchangeAsync<T>(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await accessToken(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new CentralResult<T>(
                CentralOutcome.Unauthorized, Detail: "Nobody is signed in. Sign in while there is connectivity.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await ExchangeAsync<T>(request, cancellationToken);
    }

    private async Task<CentralResult<T>> ExchangeAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        string body;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return new CentralResult<T>(CentralOutcome.Unreachable, Detail: ex.Message);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The HttpClient's own timeout, not the caller cancelling.
            return new CentralResult<T>(CentralOutcome.Unreachable, Detail: $"No answer in time: {ex.Message}");
        }

        using (response)
        {
            return Classify<T>(response, body);
        }
    }

    private static CentralResult<T> Classify<T>(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        var retryAfter = RetryAfterOf(response);
        using var document = TryParse(body);
        var root = document?.RootElement;

        // A District node: its own shape, carrying the centre's answer once it has one.
        if (root is { ValueKind: JsonValueKind.Object } node && node.TryGetProperty("central", out _)
            && node.TryGetProperty("status", out var nodeStatus))
        {
            return FromDistrict<T>(node, nodeStatus.GetString(), status);
        }

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created && root is { } ok)
        {
            return Data<T>(ok) is { } value
                ? new CentralResult<T>(CentralOutcome.Succeeded, value, status)
                : new CentralResult<T>(CentralOutcome.Unreachable, StatusCode: status,
                    Detail: "The answer could not be read. Try again.");
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new CentralResult<T>(
                CentralOutcome.Unauthorized, StatusCode: status, Errors: Errors(root),
                Detail: "The centre did not accept the sign-in. Sign in again."),

            // "Already in progress": the same transaction is being processed.
            HttpStatusCode.Conflict when retryAfter is not null => new CentralResult<T>(
                CentralOutcome.InProgress, StatusCode: status, RetryAfter: retryAfter),

            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => new CentralResult<T>(
                CentralOutcome.Unreachable, StatusCode: status, RetryAfter: retryAfter),

            _ when status is >= 400 and < 500 => new CentralResult<T>(
                CentralOutcome.Refused, StatusCode: status, Errors: Errors(root)),

            _ => new CentralResult<T>(CentralOutcome.Unreachable, StatusCode: status, RetryAfter: retryAfter),
        };
    }

    private static CentralResult<T> FromDistrict<T>(JsonElement node, string? nodeStatus, int status)
    {
        var lastError = node.TryGetProperty("lastError", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;
        var central = node.TryGetProperty("central", out var answer) && answer.ValueKind == JsonValueKind.Object
            ? answer
            : (JsonElement?)null;

        return nodeStatus switch
        {
            "Forwarded" when central is { } forwarded && Data<T>(forwarded) is { } value
                => new CentralResult<T>(CentralOutcome.Succeeded, value, status),

            "Rejected" => new CentralResult<T>(
                CentralOutcome.Refused, StatusCode: status, Errors: Errors(central), Detail: lastError),

            _ => new CentralResult<T>(CentralOutcome.Held, StatusCode: status, Detail: lastError),
        };
    }

    /// <summary>The <c>data</c> of the centre's <c>{ meta, data }</c> envelope.</summary>
    private static T? Data<T>(JsonElement envelope)
        => envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("data", out var data)
            ? data.Deserialize<T>(ClientJson.Options)
            : default;

    private static IReadOnlyList<ApiError>? Errors(JsonElement? envelope)
    {
        if (envelope is not { ValueKind: JsonValueKind.Object } root)
        {
            return null;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            return errors.Deserialize<List<ApiError>>(ClientJson.Options);
        }

        // A District node refusing a malformed batch: { "error": "..." }.
        return root.TryGetProperty("error", out var single) && single.ValueKind == JsonValueKind.String
            ? [new ApiError("request", single.GetString()!)]
            : null;
    }

    private static JsonDocument? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
        => response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
}
