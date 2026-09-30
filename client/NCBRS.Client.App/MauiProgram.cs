#if ANDROID
using ZXing.Net.Maui.Controls;
#endif
using Microsoft.Extensions.Logging;
using NCBRS.Client.App.Services;

namespace NCBRS.Client.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

#if ANDROID
        // The camera QR reader for checking certificates. Android only: the
        // Windows head checks by typing or pasting the code.
        builder.UseBarcodeReader();
#endif

        // The system browser for signing in, returning to the app the way each
        // platform allows: its own scheme on Android, the loopback on Windows.
#if ANDROID
        builder.Services.AddSingleton<ISignInBrowser, AndroidSignInBrowser>();
        builder.Services.AddSingleton<IDocumentPrinter, AndroidPagePrinter>();
#elif WINDOWS
        builder.Services.AddSingleton<ISignInBrowser, LoopbackSignInBrowser>();
#endif

        // One owner of the tablet's state for the life of the app: the store,
        // the session rebuilt from it, and the sign-ins. See DeviceHost.
        builder.Services.AddSingleton<DeviceHost>();

        return builder.Build();
    }
}
