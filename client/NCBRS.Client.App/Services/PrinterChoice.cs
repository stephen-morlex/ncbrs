namespace NCBRS.Client.App.Services;

/// <summary>
/// Which printer this tablet prints to: Android's print system (a page, or a
/// PDF), or a Bluetooth thermal printer paired in Android's settings, with its
/// paper width. A setting of the tablet, not a record, so it lives in
/// Preferences like the language.
/// </summary>
public sealed record PrinterChoice(string? BluetoothAddress, string? Name, int WidthDots)
{
    /// <summary>58 mm paper at 203 dpi: 48 mm of printable width.</summary>
    public const int Dots58 = 384;

    /// <summary>80 mm paper at 203 dpi: 72 mm of printable width.</summary>
    public const int Dots80 = 576;

    private const string AddressKey = "ncbrs-printer-address";
    private const string NameKey = "ncbrs-printer-name";
    private const string WidthKey = "ncbrs-printer-width";

    public static PrinterChoice Page { get; } = new(null, null, 0);

    public bool IsBluetooth => BluetoothAddress is not null;

    public static PrinterChoice Load()
        => Preferences.Default.Get<string?>(AddressKey, null) is { } address
            ? new PrinterChoice(address, Preferences.Default.Get<string?>(NameKey, null), Preferences.Default.Get(WidthKey, Dots58))
            : Page;

    public void Save()
    {
        if (BluetoothAddress is null)
        {
            Preferences.Default.Remove(AddressKey);
            Preferences.Default.Remove(NameKey);
            Preferences.Default.Remove(WidthKey);
            return;
        }

        Preferences.Default.Set(AddressKey, BluetoothAddress);
        Preferences.Default.Set(NameKey, Name ?? BluetoothAddress);
        Preferences.Default.Set(WidthKey, WidthDots);
    }
}
