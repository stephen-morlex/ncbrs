using Microsoft.Extensions.Logging;

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

        // Where the shell wires the client-core. In a real build these are
        // constructed from the encrypted local store (B2) after the registrar
        // unlocks (B3):
        //   - the device's persisted private key            -> DeviceSigner.FromPrivateKey(...)
        //   - the granted BRN block + saved cursor          -> new DeviceBrnAllocator(...)
        //   - the persisted outbox                          -> new SyncOutbox(deviceId, facilityId, restore)
        //   - all composed into a FacilityClient for the session.
        // The shell adds an HTTP client to POST FacilityClient.BuildSignedUpload()
        // and a certificate printer for FacilityClient's provisional slips.
        //
        // builder.Services.AddSingleton<ILocalStore, EncryptedSqliteStore>();
        // builder.Services.AddSingleton<DeviceSessionFactory>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
