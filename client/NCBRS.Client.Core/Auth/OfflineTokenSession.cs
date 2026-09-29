using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NCBRS.Client.Localization;
using NCBRS.Client.Network;

namespace NCBRS.Client.Auth;

/// <summary>Where the realm's OpenID Connect endpoints are, and which client the tablet is.</summary>
public sealed record OidcEndpoints(Uri Authorization, Uri Token, Uri Revocation, string ClientId = "ncbrs-device")
{
    /// <summary>The Keycloak endpoints of a realm, from its URL (e.g. <c>https://id.example/realms/ncbrs/</c>).</summary>
    public static OidcEndpoints ForKeycloakRealm(Uri realm, string clientId = "ncbrs-device")
    {
        var root = realm.AbsoluteUri.EndsWith('/') ? realm : new Uri(realm.AbsoluteUri + "/");
        return new OidcEndpoints(
            new Uri(root, "protocol/openid-connect/auth"),
            new Uri(root, "protocol/openid-connect/token"),
            new Uri(root, "protocol/openid-connect/revoke"),
            clientId);
    }
}

/// <summary>
/// The PKCE pair and state for one interactive sign-in. The verifier stays on
/// the device; only its hash travels in the browser.
/// </summary>
public sealed record PkceChallenge(string Verifier, string Challenge, string State)
{
    public static PkceChallenge Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new PkceChallenge(
            verifier,
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>How an interactive sign-in ended.</summary>
public sealed record SignInResult(bool SignedIn, string? Problem = null);

/// <summary>
/// A registrar's sign-in on a tablet, held as a Keycloak <b>offline token</b>.
///
/// A post can be out of contact for weeks, far longer than any ordinary session
/// lasts (30 minutes idle in the realm), so the tablet signs in once — in a
/// browser, authorization code with PKCE — asking for <c>offline_access</c>,
/// and keeps the offline token that comes back in its encrypted store. At each
/// connectivity window it trades that for a short-lived access token. The
/// realm lets it lapse after 60 days unused and 180 days in all, after which
/// the registrar signs in again; only registrars and community health workers
/// may hold one.
///
/// Three things this has to get right:
///
/// - <b>Keycloak rotates it on every use.</b> Each refresh returns a new offline
///   token, which is persisted at once; a device that kept the old one would
///   eventually hold a dead sign-in.
/// - <b>"Sign in again" is not "no network".</b> An offline session that has
///   lapsed or been revoked answers <c>invalid_grant</c>: the token is dropped
///   and <see cref="GetAccessTokenAsync"/> returns null, which the network layer
///   reports as <see cref="CentralOutcome.Unauthorized"/>. No answer at all is
///   thrown as <see cref="HttpRequestException"/>, which it reports as
///   <see cref="CentralOutcome.Unreachable"/> — telling a registrar at a village
///   post to sign in again because the mast is down would be wrong twice.
/// - <b>Signing out revokes it</b>, at Keycloak, not just on the device.
///
/// The interactive half — opening the browser and catching the redirect — is
/// the shell's; <see cref="AuthorizationUrl"/> and <see cref="RedeemAsync"/> are
/// everything either side of it.
/// </summary>
public sealed class OfflineTokenSession(
    HttpClient http,
    OidcEndpoints endpoints,
    string? offlineToken,
    Func<string?, CancellationToken, Task> persistOfflineToken)
{
    public const string Scope = "openid offline_access";

    private string? _offlineToken = offlineToken;
    private string? _accessToken;
    private DateTime _accessExpiresAtUtc = DateTime.MinValue;

    public bool SignedIn => _offlineToken is not null;

    /// <summary>Why the last attempt to use the sign-in did not work, for the registrar.</summary>
    public string? LastProblem { get; private set; }

    /// <summary>This session as the network layer's token source.</summary>
    public AccessTokenProvider AccessToken => GetAccessTokenAsync;

    /// <summary>Where to send the registrar's browser to sign in.</summary>
    public Uri AuthorizationUrl(PkceChallenge pkce, Uri redirectUri)
        => AuthorizationCodeFlow.Url(endpoints, pkce, redirectUri, Scope);

    /// <summary>
    /// Complete a sign-in: trade the code the browser came back with for the
    /// offline token. Refused if the account cannot hold one — a tablet signed
    /// in on an ordinary session would silently stop syncing within the hour.
    /// </summary>
    public async Task<SignInResult> RedeemAsync(
        string code, PkceChallenge pkce, Uri redirectUri, CancellationToken cancellationToken = default)
    {
        using var response = await AuthorizationCodeFlow.ExchangeAsync(http, endpoints, code, pkce, redirectUri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new SignInResult(false, await AuthorizationCodeFlow.RefusalAsync(response, cancellationToken));
        }

        return await AcceptAsync(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Adopt a token response obtained some other way — the test harness signs
    /// in with the password grant, which a production realm does not offer.
    /// </summary>
    public Task<SignInResult> AcceptAsync(JsonElement tokenResponse, CancellationToken cancellationToken = default)
    {
        var refresh = tokenResponse.TryGetProperty("refresh_token", out var value) ? value.GetString() : null;
        if (refresh is null || TokenType(refresh) != "Offline")
        {
            LastProblem = Strings.SignIn_CannotHoldOffline;
            return Task.FromResult(new SignInResult(false, LastProblem));
        }

        return AdoptAsync(tokenResponse, refresh, cancellationToken);
    }

    /// <summary>
    /// A current access token: the cached one, or a fresh one traded for the
    /// offline token. Null when nobody is signed in or the sign-in has ended;
    /// throws <see cref="HttpRequestException"/> when Keycloak cannot be reached.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessExpiresAtUtc.AddMinutes(-1))
        {
            return _accessToken;
        }

        if (_offlineToken is null)
        {
            return null;
        }

        using var response = await http.PostAsync(endpoints.Token, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = endpoints.ClientId,
            ["refresh_token"] = _offlineToken,
        }), cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await AdoptAsync(body, body.TryGetProperty("refresh_token", out var rotated) ? rotated.GetString() : null, cancellationToken);
            return _accessToken;
        }

        var error = await ErrorOf(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest && error == "invalid_grant")
        {
            // The offline session lapsed (60 days unused, 180 in all) or was
            // revoked. Only signing in again resolves it.
            await ForgetAsync(cancellationToken);
            LastProblem = Strings.SignIn_Ended;
            return null;
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new HttpRequestException($"The identity provider could not answer ({(int)response.StatusCode}).");
        }

        LastProblem = Language.Format(Strings.SignIn_RenewRefused, (int)response.StatusCode, error);
        return null;
    }

    /// <summary>Sign out: revoke the offline token at Keycloak, then forget it here.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (_offlineToken is { } token)
        {
            try
            {
                using var _ = await http.PostAsync(endpoints.Revocation, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = endpoints.ClientId,
                    ["token"] = token,
                    ["token_type_hint"] = "refresh_token",
                }), cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Offline: forgotten here regardless. It lapses at Keycloak after
                // 60 days unused, and a district can revoke it sooner.
            }
        }

        await ForgetAsync(cancellationToken);
    }

    private async Task<SignInResult> AdoptAsync(JsonElement body, string? refresh, CancellationToken cancellationToken)
    {
        _accessToken = body.GetProperty("access_token").GetString();
        _accessExpiresAtUtc = DateTime.UtcNow.AddSeconds(body.TryGetProperty("expires_in", out var lifetime) ? lifetime.GetInt32() : 300);
        LastProblem = null;

        // Keycloak hands back a new offline token on every use: keep the latest.
        if (refresh is not null && refresh != _offlineToken)
        {
            _offlineToken = refresh;
            await persistOfflineToken(refresh, cancellationToken);
        }

        return new SignInResult(true);
    }

    private async Task ForgetAsync(CancellationToken cancellationToken)
    {
        _offlineToken = null;
        _accessToken = null;
        _accessExpiresAtUtc = DateTime.MinValue;
        await persistOfflineToken(null, cancellationToken);
    }

    /// <summary>The <c>typ</c> claim of a Keycloak refresh token: <c>Offline</c> for an offline token.</summary>
    public static string? TokenType(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            return claims.RootElement.TryGetProperty("typ", out var typ) ? typ.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    internal static async Task<string?> ErrorOf(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
