using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Printing;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Printing a birth's certificate (B7): the registry signs it, so this needs
/// signal. The registrar types the number from the family's slip, or pastes
/// the slip's code. What is printed is what the certificate's own code proves,
/// checked on this tablet first (<see cref="CertificateForPrint"/>).
/// </summary>
public sealed class PrintCertificatePage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly Entry _brn = new()
    {
        Placeholder = Strings.Check_Brn,
        Keyboard = Keyboard.Numeric,
        FlowDirection = FlowDirection.LeftToRight,
    };
    private readonly Label _done = new() { FontSize = 15 };

    public PrintCertificatePage(DeviceHost host) : base(Strings.Register_PrintCertificate)
    {
        _host = host;

        var print = Ui.PrimaryButton(Strings.Print_CertFetch);
        print.Clicked += async (_, _) => await RunAsync(PrintAsync);
        var back = Ui.GhostButton(Strings.Common_GoBack);
        // Unlocked, the bottom bar is the way back; before, this is the only one.
        back.IsVisible = host.UnlockedAs is null;
        back.Clicked += (_, _) => Flow.Advance(host);

        BuildFor(host, Pages.Section.More, Heading(Strings.Register_PrintCertificate), Note(Strings.Print_CertNote), _brn, print, Status, Busy, _done, back);
    }

    private async Task PrintAsync()
    {
        _done.Text = "";
        var typed = _brn.Text?.Trim() ?? "";
        var brn = SlipCode.TryRead(typed, out var fromSlip) ? fromSlip : Language.WesternDigits(typed);

        if (brn.Length == 0)
        {
            await ShowProblemAsync(Strings.Print_EnterBrn);
            return;
        }

        if (_host.Printer is null)
        {
            await ShowProblemAsync(Strings.Print_Unavailable);
            return;
        }

        var certificate = await _host.FetchCertificateAsync(brn);
        if (certificate.Document is null)
        {
            await ShowProblemAsync(certificate.Problem ?? Strings.Print_NotGenuine);
            return;
        }

        if (await _host.Printer.PrintAsync(certificate.Document) is { } problem)
        {
            await ShowProblemAsync(problem);
            return;
        }

        _done.Text = certificate.Reprint ? Strings.Print_Sent + "\n" + Strings.Print_Reprinted : Strings.Print_Sent;
    }
}
