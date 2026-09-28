using Android.App;
using Android.Content;
using Android.Content.PM;

namespace NCBRS.Client.App;

/// <summary>Receives the identity provider's redirect and hands it to WebAuthenticator.</summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = AndroidSignInBrowser.CallbackScheme,
    DataHost = "auth")]
public sealed class SignInCallbackActivity : WebAuthenticatorCallbackActivity
{
}
