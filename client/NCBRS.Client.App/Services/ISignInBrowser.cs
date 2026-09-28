namespace NCBRS.Client.App.Services;

/// <summary>
/// The browser half of signing in: open the identity provider's page in the
/// system browser — never an embedded web view, which would let the app read
/// the password — and bring back the code and state it redirects with.
/// </summary>
public interface ISignInBrowser
{
    /// <summary>Where the identity provider sends the browser back to. Registered on the realm's client.</summary>
    Uri RedirectUri { get; }

    /// <summary>The code and state from the redirect, or null if the person closed the browser.</summary>
    Task<SignInCallback?> SignInAsync(Uri authorizationUrl, CancellationToken cancellationToken = default);
}

public sealed record SignInCallback(string Code, string State);
