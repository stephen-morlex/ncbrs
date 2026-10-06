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
        // Letters too: a composed number reads SS-JTH-2026-000014-0, which a
        // numeric keypad cannot type. Capitals, as the number is printed.
        Keyboard = Keyboard.Create(KeyboardFlags.CapitalizeCharacter),
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        FlowDirection = FlowDirection.LeftToRight,
    };
    private readonly Label _done = Ui.Body("");

    /// <param name="brn">The number to print, when opened from a birth in Records.</param>
    public PrintCertificatePage(DeviceHost host, string? brn = null) : base(Strings.Register_PrintCertificate)
    {
        _host = host;
        _brn.Text = brn;

        var print = Ui.PrimaryButton(Strings.Print_CertFetch);
        print.Clicked += async (_, _) => await RunAsync(PrintAsync);
        var back = Ui.GhostButton(Strings.Common_GoBack);
        // Unlocked, the bottom bar is the way back; before, this is the only one.
        back.IsVisible = host.UnlockedAs is null;
        back.Clicked += (_, _) => Flow.Advance(host);

        BuildFor(host, Pages.Section.More, Heading(Strings.Register_PrintCertificate), Note(Strings.Print_CertNote), Ui.Labeled(_brn), print, Status, Busy, _done, back);
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
        await _host.MarkPrintedAsync(brn);
    }
}
