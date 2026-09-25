using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NCBRS.Devices;

namespace NCBRS.District.Services;

public class CentralApiOptions
{
    public const string SectionName = "Central";

    public string BaseUrl { get; set; } = "http://localhost:5259";

    /// <summary>Keycloak token endpoint the node authenticates against.</summary>
    public string TokenEndpoint { get; set; } =
        "http://localhost:8080/realms/ncbrs/protocol/openid-connect/token";

    public string ClientId { get; set; } = "ncbrs-device";

    public string? ClientSecret { get; set; }

    /// <summary>
    /// The node's own service account. Supply via environment variable or a
    /// secret store, never appsettings.json -- these credentials can file
    /// births for an entire district.
    /// </summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// The part of a forward's timeout that does not depend on its size, and
    /// the whole timeout for a token request.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Added to <see cref="Timeout"/> for every record in the batch.
    ///
    /// A single timeout for every batch was the bug: the centre processes a
    /// batch in one transaction, so its time grows with the record count, and
    /// a flat 20 s that suits a 25-record batch cannot fit a 500-record one
    /// under load. A timeout the batch cannot fit in is not a timeout, it is a
    /// guarantee the batch never lands. At the default, a full 500-record batch
    /// is allowed two minutes.
    /// </summary>
    public TimeSpan TimeoutPerRecord { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The ceiling on any one attempt, however many times it has doubled. A
    /// node must still notice, eventually, that a request is never coming back.
    /// </summary>
    public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long to allow one attempt at a batch of <paramref name="records"/>
    /// that has already timed out <paramref name="consecutiveTimeouts"/> times
    /// in a row. Doubles with each timeout, so an estimate that proves too low
    /// under real load corrects itself instead of failing identically forever.
    /// </summary>
    public TimeSpan AttemptTimeout(int records, int consecutiveTimeouts)
    {
        var estimate = Timeout + TimeoutPerRecord * Math.Max(0, records);
        var scaled = estimate * Math.Pow(2, Math.Clamp(consecutiveTimeouts, 0, 16));

        return scaled < MaxTimeout ? scaled : MaxTimeout;
    }
}

public record CentralForwardResult(
    bool Reached,
    int? StatusCode = null,
    string? Body = null,
    string? Error = null,
    bool TimedOut = false,
    TimeSpan? RetryAfter = null)
{
    /// <summary>The centre accepted it. Its answer is in Body.</summary>
    public bool Accepted => Reached && StatusCode is >= 200 and < 300;

    /// <summary>
    /// The centre refused it for a reason retrying will not fix. Anything
    /// else -- a timeout, a 5xx, a dropped connection -- stays queued,
    /// because the difference between "the centre said no" and "the centre
    /// did not answer" is the difference between discarding a birth and
    /// waiting for the link to come back.
    ///
    /// A 4xx that carries <c>Retry-After</c> is "not yet", not "no". The centre
    /// sends it with the 409 meaning "this transaction is already being
    /// processed" -- the batch arriving by two routes at once, or a retry
    /// overlapping the attempt it follows. Treating that as a refusal marked
    /// batches the centre had registered as rejected.
    /// </summary>
    public bool PermanentlyRejected =>
        Reached && StatusCode is >= 400 and < 500 and not 408 and not 429 && RetryAfter is null;
}

/// <summary>
/// The node's connection to the national tier.
///
/// It authenticates as itself rather than reusing the device's token, for
/// two reasons. Tokens are short-lived, and a batch may sit here for days
/// during an outage — a node hoarding bearer tokens on district hardware so
/// it can replay them later is a worse exposure than the problem it solves.
/// And attribution stays correct without it: each record already carries the
/// registrar who authored it, which the centre validates, while the batch is
/// genuinely uploaded by the node. Author and uploader stay distinguishable,
/// which is exactly what SyncBatch was shaped for.
///
/// Which *device* produced the batch is a different claim, and the node's own
/// token cannot make it. That is what the device's signature is for, and the
/// node forwards it untouched.
/// </summary>
public class CentralApiClient(
    HttpClient http,
    IOptions<CentralApiOptions> options,
    ILogger<CentralApiClient> logger)
{
    private readonly CentralApiOptions _options = options.Value;

    private string? _token;
    private DateTime _tokenExpiresAtUtc = DateTime.MinValue;

    /// <summary>
    /// Sends one batch, allowing it <paramref name="timeout"/>.
    ///
    /// <paramref name="cancellationToken"/> is for the node shutting down, and
    /// is rethrown. Running out of <paramref name="timeout"/> is an outcome,
    /// reported as <see cref="CentralForwardResult.TimedOut"/>, because the
    /// caller has to treat it differently from a centre that never answered.
    /// </summary>
    public async Task<CentralForwardResult> ForwardAsync(
        string payload,
        string? deviceSignature,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        string token;

        try
        {
            using var tokenTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tokenTimeout.CancelAfter(_options.Timeout);

            token = await TokenAsync(tokenTimeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            // No token means no centre. Indistinguishable from an outage from
            // the node's point of view, and handled the same way: keep the
            // batch.
            return new CentralForwardResult(false, Error: $"Could not obtain a token: {ex.Message}");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/api/Sync/batches")
        {
            // The bytes the device sent, which are the bytes its signature
            // covers. The centre verifies the raw body byte for byte.
            Content = new StringContent(payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (!string.IsNullOrEmpty(deviceSignature))
        {
            request.Headers.TryAddWithoutValidation(DeviceSignature.HeaderName, deviceSignature);
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(timeout);

        try
        {
            using var response = await http.SendAsync(request, attempt.Token);
            var body = await response.Content.ReadAsStringAsync(attempt.Token);

            return new CentralForwardResult(
                true, (int)response.StatusCode, body, RetryAfter: RetryAfterOf(response));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline, not a shutdown. The centre was reached and was
            // still working: it has now cancelled and rolled back the batch.
            logger.LogWarning(
                "Central tier did not finish a batch within {Timeout}; it will be retried with longer", timeout);

            return new CentralForwardResult(
                false,
                Error: $"The centre did not finish processing the batch within {timeout.TotalSeconds:0} s "
                       + "and discarded the attempt. It will be retried with a longer timeout.",
                TimedOut: true);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Central tier unreachable while forwarding a batch");

            return new CentralForwardResult(false, Error: ex.Message);
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date
            ? date - DateTimeOffset.UtcNow
            : null;
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        // A minute of headroom: a token that expires mid-forward costs a
        // whole retry cycle.
        if (_token is not null && DateTime.UtcNow < _tokenExpiresAtUtc.AddMinutes(-1))
        {
            return _token;
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", _options.ClientId)
        };

        if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            form.Add(new("client_secret", _options.ClientSecret));
        }

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            form.Add(new("grant_type", "password"));
            form.Add(new("username", _options.Username));
            form.Add(new("password", _options.Password ?? string.Empty));
        }
        else
        {
            form.Add(new("grant_type", "client_credentials"));
        }

        using var response = await http.PostAsync(
            _options.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        _token = document.RootElement.GetProperty("access_token").GetString()!;

        var lifetime = document.RootElement.TryGetProperty("expires_in", out var expires)
            ? expires.GetInt32()
            : 300;

        _tokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(lifetime);

        return _token;
    }
}
