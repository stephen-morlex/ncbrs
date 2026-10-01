using Android;
using Android.Bluetooth;
using Android.Content;
using Java.Util;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Printing;

namespace NCBRS.Client.App;

/// <summary>
/// Connecting to a paired printer. Android 12 and later ask for it at run
/// time; earlier versions granted it at install.
/// </summary>
public sealed class BluetoothConnectPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions
        => OperatingSystem.IsAndroidVersionAtLeast(31)
            ? [(Manifest.Permission.BluetoothConnect, true)]
            : [];
}

/// <summary>
/// A portable ESC/POS printer over Bluetooth's serial profile. Pairing is
/// done once in Android's own Bluetooth settings; the app lists what is paired
/// and never scans, so it needs no location permission and finds nothing it
/// was not given.
/// </summary>
public static class BluetoothThermalPrinter
{
    // The Serial Port Profile: what portable receipt printers speak.
    private static readonly UUID SerialPort = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    public static async Task<(IReadOnlyList<(string Name, string Address)> Printers, string? Problem)> PairedAsync()
    {
        if (Adapter() is not { } adapter)
        {
            return ([], Strings.Printer_NoBluetooth);
        }

        if (await Permissions.RequestAsync<BluetoothConnectPermission>() != PermissionStatus.Granted)
        {
            return ([], Strings.Printer_BluetoothDenied);
        }

        var paired = adapter.BondedDevices?
            .Select(device => (Name: device.Name ?? device.Address ?? "", Address: device.Address ?? ""))
            .Where(device => device.Address.Length > 0)
            .OrderBy(device => device.Name)
            .ToList() ?? [];

        return (paired, paired.Count == 0 ? Strings.Printer_NoneBonded : null);
    }

    public static async Task<string?> PrintAsync(PrintedDocument document, PrinterChoice printer)
    {
        if (Adapter() is not { } adapter)
        {
            return Strings.Printer_NoBluetooth;
        }

        if (await Permissions.RequestAsync<BluetoothConnectPermission>() != PermissionStatus.Granted)
        {
            return Strings.Printer_BluetoothDenied;
        }

        var job = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            using var bitmap = ThermalRenderer.Draw(document, printer.WidthDots);
            return EscPos.Job(ThermalRenderer.Dots(bitmap));
        });

        try
        {
            await Task.Run(async () =>
            {
                var device = adapter.GetRemoteDevice(printer.BluetoothAddress!)!;
                using var socket = device.CreateRfcommSocketToServiceRecord(SerialPort)!;
                await socket.ConnectAsync();

                // In pieces, with a pause: a cheap printer's buffer is small,
                // and one that overflows drops lines without saying so.
                var output = socket.OutputStream!;
                for (var offset = 0; offset < job.Length; offset += 1024)
                {
                    await output.WriteAsync(job.AsMemory(offset, Math.Min(1024, job.Length - offset)));
                    await output.FlushAsync();
                    await Task.Delay(20);
                }

                // Closing at once can cut off what the printer has not read yet.
                await Task.Delay(500);
            });
            return null;
        }
        catch (Exception ex) when (ex is Java.IO.IOException or System.IO.IOException or Java.Lang.SecurityException)
        {
            return Language.Format(Strings.Printer_NotReached, printer.Name, ex.Message);
        }
    }

    private static BluetoothAdapter? Adapter()
        => (Platform.AppContext.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;
}

/// <summary>Whichever printer the tablet is set to: a paired thermal printer, or Android's print system.</summary>
public sealed class ChosenPrinter : IDocumentPrinter
{
    private readonly AndroidPagePrinter _page = new();

    public Task<string?> PrintAsync(PrintedDocument document)
        => PrinterChoice.Load() is { IsBluetooth: true } thermal
            ? BluetoothThermalPrinter.PrintAsync(document, thermal)
            : _page.PrintAsync(document);
}
