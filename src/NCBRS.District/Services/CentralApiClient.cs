using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

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

    /// <summary>How long a forward may take before it is treated as unreachable.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}

public record CentralForwardResult(
    bool Reached,
    int? StatusCode = null,
    string? Body = null,
    string? Error = null)
{
    /// <summary>The centre accepted it. Its answer is in Body.</summary>
    public bool Accepted => Reached && StatusCode is >= 200 and < 300;

    /// <summary>
    /// The centre refused it for a reason retrying will not fix. Anything
    /// else -- a timeout, a 5xx, a dropped connection -- stays queued,
    /// because the difference between "the centre said no" and "the centre
    /// did not answer" is the difference between discarding a birth and
    /// waiting for the link to come back.
    /// </summary>
    public bool PermanentlyRejected => Reached && StatusCode is >= 400 and < 500 and not 408 and not 429;
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
/// </summary>
public class CentralApiClient(
    HttpClient http,
    IOptions<CentralApiOptions> options,
    ILogger<CentralApiClient> logger)
{
    private readonly CentralApiOptions _options = options.Value;

    private string? _token;
    private DateTime _tokenExpiresAtUtc = DateTime.MinValue;

    public async Task<CentralForwardResult> ForwardAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        string token;

        try
        {
            token = await TokenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // No token means no centre. Indistinguishable from an outage from
            // the node's point of view, and handled the same way: keep the
            // batch.
            return new CentralForwardResult(false, Error: $"Could not obtain a token: {ex.Message}");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/api/Sync/batches")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return new CentralForwardResult(true, (int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Central tier unreachable while forwarding a batch");

            return new CentralForwardResult(false, Error: ex.Message);
        }
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
