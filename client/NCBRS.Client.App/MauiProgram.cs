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
                // Geist for Latin text, Geist Mono for registration numbers,
                // Noto Sans Arabic for Arabic (all SIL OFL; see Resources/Fonts/Licenses).
                fonts.AddFont("Geist-Regular.ttf", "GeistRegular");
                fonts.AddFont("Geist-SemiBold.ttf", "GeistSemiBold");
                fonts.AddFont("GeistMono-Regular.ttf", "GeistMonoRegular");
                fonts.AddFont("GeistMono-SemiBold.ttf", "GeistMonoSemiBold");
                fonts.AddFont("NotoSansArabic-Regular.ttf", "NotoSansArabicRegular");
                fonts.AddFont("NotoSansArabic-SemiBold.ttf", "NotoSansArabicSemiBold");
            });

#if ANDROID
        // Inputs are drawn in their own bordered box (Ui.Input), so Android's
        // underline beneath each one is removed rather than doubled.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
            handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent));
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
            handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent));
        Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
            handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent));
#endif

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
        builder.Services.AddSingleton<IDocumentPrinter, ChosenPrinter>();
#elif WINDOWS
        builder.Services.AddSingleton<ISignInBrowser, LoopbackSignInBrowser>();
#endif

        // One owner of the tablet's state for the life of the app: the store,
        // the session rebuilt from it, and the sign-ins. See DeviceHost.
        builder.Services.AddSingleton<DeviceHost>();

        return builder.Build();
    }
}
