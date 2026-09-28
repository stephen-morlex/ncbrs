using System.Net;
using System.Text;
using System.Web;
using NCBRS.Client.App.Services;

namespace NCBRS.Client.App;

/// <summary>
/// Windows (the desktop dev loop): the system browser, returning to a listener
/// on the loopback address, as RFC 8252 describes for native apps. A fixed
/// port, registered on the realm's client, because Keycloak matches the whole
/// redirect URI.
/// </summary>
public sealed class LoopbackSignInBrowser : ISignInBrowser
{
    public Uri RedirectUri { get; } = new("http://127.0.0.1:53682/auth");

    public async Task<SignInCallback?> SignInAsync(Uri authorizationUrl, CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:53682/auth/");
        listener.Start();

        await Launcher.Default.OpenAsync(authorizationUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);

        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        var page = Encoding.UTF8.GetBytes("<html><body><p>Signed in. You can return to NCBRS.</p></body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        await context.Response.OutputStream.WriteAsync(page, cancellationToken);
        context.Response.Close();

        return query["code"] is { } code && query["state"] is { } state ? new SignInCallback(code, state) : null;
    }
}
