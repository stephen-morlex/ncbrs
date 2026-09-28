using NCBRS.Client.App.Services;

namespace NCBRS.Client.App;

/// <summary>
/// Android: the system browser (a Custom Tab) through MAUI's WebAuthenticator,
/// returning to the app on its own scheme.
/// </summary>
public sealed class AndroidSignInBrowser : ISignInBrowser
{
    public const string CallbackScheme = "ss.gov.ncbrs.client";

    /// <summary>
    /// With a path on purpose: <c>new Uri("…://auth").AbsoluteUri</c> is
    /// <c>…://auth/</c>, a trailing slash .NET adds and Keycloak does not, so a
    /// bare host never matches the URI registered on the realm's client.
    /// </summary>
    public Uri RedirectUri { get; } = new($"{CallbackScheme}://auth/callback");

    public async Task<SignInCallback?> SignInAsync(Uri authorizationUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = authorizationUrl,
                CallbackUrl = RedirectUri,
            });

            return result.Properties.TryGetValue("code", out var code) && result.Properties.TryGetValue("state", out var state)
                ? new SignInCallback(code, state)
                : null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }
}
