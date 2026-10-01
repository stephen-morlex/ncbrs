using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Printing;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Choosing the tablet's printer (B7): Android's print system (an office
/// printer, or a PDF), or a Bluetooth thermal printer already paired in the
/// tablet's settings, with its paper width. A test page proves the choice
/// before a family is waiting on it.
/// </summary>
public sealed class PrinterPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly Picker _printer = new() { Title = Strings.Printer_Title };
    private readonly Picker _width = new() { Title = Strings.Printer_Paper };
    private readonly Label _current = new() { FontSize = 15 };
    private List<(string Name, string? Address)> _choices = [(Strings.Printer_Page, null)];

    public PrinterPage(DeviceHost host) : base(Strings.Printer_Title)
    {
        _host = host;
        _width.ItemsSource = new[] { Strings.Printer_58, Strings.Printer_80 };
        _width.SelectedIndex = PrinterChoice.Load().WidthDots == PrinterChoice.Dots80 ? 1 : 0;

        var save = new Button { Text = Strings.Printer_Save };
        save.Clicked += (_, _) => Save();
        var test = new Button { Text = Strings.Printer_Test, BackgroundColor = Colors.DarkSlateGray };
        test.Clicked += async (_, _) => await RunAsync(TestAsync);
        var back = new Button { Text = Strings.Common_GoBack, BackgroundColor = Colors.Gray };
        back.Clicked += (_, _) => Flow.Advance(host);

        var views = new List<View> { Heading(Strings.Printer_Title), _current, Note(Strings.Printer_Note), _printer, _width, save, test };

#if DEBUG
        // For checking the thermal layout where no printer is in reach: the
        // exact image a printer would get, saved for adb to pull. Debug builds only.
        var preview = new Button { Text = "Save a thermal preview (debug)", BackgroundColor = Colors.Gray };
        preview.Clicked += async (_, _) => await RunAsync(PreviewAsync);
        views.Add(preview);
#endif

        views.AddRange([Status, Busy, back]);
        Build([.. views]);
        ShowCurrent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        var (paired, problem) = await BluetoothThermalPrinter.PairedAsync();
        _choices = [(Strings.Printer_Page, null), .. paired.Select(printer => (printer.Name, (string?)printer.Address))];
        Status.Text = problem ?? "";
#else
        await Task.CompletedTask;
#endif
        _printer.ItemsSource = _choices.Select(choice => choice.Name).ToList();
        var saved = PrinterChoice.Load();
        _printer.SelectedIndex = Math.Max(0, _choices.FindIndex(choice => choice.Address == saved.BluetoothAddress));
    }

    private void Save()
    {
        var (name, address) = _choices[Math.Max(0, _printer.SelectedIndex)];
        var width = _width.SelectedIndex == 1 ? PrinterChoice.Dots80 : PrinterChoice.Dots58;
        (address is null ? PrinterChoice.Page : new PrinterChoice(address, name, width)).Save();
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        var chosen = PrinterChoice.Load();
        _current.Text = Language.Format(Strings.Printer_Current, chosen.IsBluetooth
            ? $"{chosen.Name} ({(chosen.WidthDots == PrinterChoice.Dots80 ? Strings.Printer_80 : Strings.Printer_58)})"
            : Strings.Printer_Page);
    }

    private PrintedDocument TestPage()
        => PrintedDocuments.Test(_host.EnrolledTo, _host.State.Identity?.DeviceId ?? "");

    private async Task TestAsync()
    {
        Save();
        if (_host.Printer is null)
        {
            await ShowProblemAsync(Strings.Print_Unavailable);
            return;
        }

        Status.Text = await _host.Printer.PrintAsync(TestPage()) ?? Strings.Print_Sent;
    }

#if DEBUG
    private async Task PreviewAsync()
    {
#if ANDROID
        var width = _width.SelectedIndex == 1 ? PrinterChoice.Dots80 : PrinterChoice.Dots58;
        var path = Path.Combine(FileSystem.CacheDirectory, "thermal-preview.png");
        // The dots, not the drawing: what the printer would actually receive.
        using var drawn = NCBRS.Client.App.ThermalRenderer.Draw(TestPage(), width);
        var dots = NCBRS.Client.App.ThermalRenderer.Dots(drawn);
        using var bitmap = Android.Graphics.Bitmap.CreateBitmap(
            dots.Black.Select(black => black ? unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF)).ToArray(),
            dots.Width, dots.Height, Android.Graphics.Bitmap.Config.Argb8888!)!;
        await using (var file = File.Create(path))
        {
            await bitmap.CompressAsync(Android.Graphics.Bitmap.CompressFormat.Png!, 100, file);
        }

        Status.Text = path;
#else
        await Task.CompletedTask;
#endif
    }
#endif
}
