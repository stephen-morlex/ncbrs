using Android.App;
using Android.Runtime;

namespace NCBRS.Client.App;

// Cleartext to localhost in Debug only, for the plain-HTTP dev stack. A Release
// build has no network security config, so Android refuses cleartext entirely:
// births, tokens and the device's signature never cross a link unencrypted.
#if DEBUG
[Application(NetworkSecurityConfig = "@xml/network_security_config_debug")]
#else
[Application]
#endif
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
