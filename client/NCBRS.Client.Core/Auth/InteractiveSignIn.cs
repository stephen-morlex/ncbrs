using System.Net.Http.Json;
using System.Text.Json;
using NCBRS.Client.Localization;
using NCBRS.Client.Network;

namespace NCBRS.Client.Auth;

/// <summary>
/// The authorization-code step both sign-ins share: the URL the system browser
/// opens, and the exchange of the code it comes back with. In one place so the
/// registrar's sign-in and the officer's cannot drift apart.
/// </summary>
internal static class AuthorizationCodeFlow
{
    /// <summary>
    /// Always <c>prompt=login</c>. A tablet is shared, and so is its browser:
    /// without it, the second person to sign in is silently handed the first
    /// person's Keycloak session — a registrar's tablet enrolled on the
    /// officer's account, or one registrar's births attributed to another.
    /// </summary>
    public static Uri Url(OidcEndpoints endpoints, PkceChallenge pkce, Uri redirectUri, string scope)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = endpoints.ClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = scope,
            ["state"] = pkce.State,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "login",
        };

        return new Uri($"{endpoints.Authorization.AbsoluteUri}?{string.Join("&", query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"))}");
    }

    public static Task<HttpResponseMessage> ExchangeAsync(
        HttpClient http, OidcEndpoints endpoints, string code, PkceChallenge pkce, Uri redirectUri,
        CancellationToken cancellationToken)
        => http.PostAsync(endpoints.Token, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = endpoints.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = pkce.Verifier,
        }), cancellationToken);

    public static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => Language.Format(Strings.SignIn_NotAccepted, (int)response.StatusCode, await OfflineTokenSession.ErrorOf(response, cancellationToken));
}

/// <summary>
/// A sign-in for one task and no longer: the district officer who enrols the
/// tablet at handover. Enrolling is an officer's act — a device enrolled by
/// whoever holds it would close no hole — and an officer cannot hold an
/// offline token, so this is an ordinary session, used, then ended.
///
/// Nothing here is persisted. <see cref="EndAsync"/> revokes the session at
/// Keycloak as soon as the task is done, so the officer's sign-in does not
/// outlive the handover on a tablet that stays behind at the post.
/// </summary>
public sealed class InteractiveSignIn(HttpClient http, OidcEndpoints endpoints)
{
    /// <summary>An ordinary session: no offline access.</summary>
    public const string Scope = "openid";

    private string? _accessToken;
    private string? _refreshToken;

    public bool SignedIn => _accessToken is not null;

    /// <summary>For <see cref="CentralClient"/>, while the task lasts.</summary>
    public AccessTokenProvider AccessToken => _ => Task.FromResult(_accessToken);

    public Uri AuthorizationUrl(PkceChallenge pkce, Uri redirectUri)
        => AuthorizationCodeFlow.Url(endpoints, pkce, redirectUri, Scope);

    public async Task<SignInResult> RedeemAsync(
        string code, PkceChallenge pkce, Uri redirectUri, CancellationToken cancellationToken = default)
    {
        using var response = await AuthorizationCodeFlow.ExchangeAsync(http, endpoints, code, pkce, redirectUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SignInResult(false, await AuthorizationCodeFlow.RefusalAsync(response, cancellationToken));
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        _accessToken = body.GetProperty("access_token").GetString();
        _refreshToken = body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        return new SignInResult(true);
    }

    /// <summary>End the session at Keycloak, then forget it. Offline, it is forgotten here and lapses there.</summary>
    public async Task EndAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshToken is { } token)
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
                // Forgotten here regardless; the session times out at Keycloak.
            }
        }

        _accessToken = null;
        _refreshToken = null;
    }
}
