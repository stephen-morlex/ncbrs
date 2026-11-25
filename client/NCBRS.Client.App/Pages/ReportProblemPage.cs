using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Storage;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// "Report a problem" (plan §17 item 37). The registrar says what happened; the
/// tablet adds its own state as counts and versions, shown here before it goes,
/// so nobody sends what they have not seen. A post may be days from the
/// district office, and this carries what the officer would have asked on the
/// phone. Without signal the report waits on the tablet and goes at the next
/// sync, after the births.
/// </summary>
public sealed class ReportProblemPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly Editor _what = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = ProblemReports.MaxDescription, MinimumHeightRequest = 120 };
    private readonly Button _send = Ui.PrimaryButton(Strings.Problem_Send);

    public ReportProblemPage(DeviceHost host) : base(Strings.Problem_Title)
    {
        _host = host;
        _send.Clicked += async (_, _) => await RunAsync(SendAsync);

        var diagnostics = host.Diagnose();
        var facts = new VerticalStackLayout
        {
            Spacing = Space.Xs,
            Children =
            {
                Fact($"{Language.Format(Strings.Footer_Version, diagnostics.AppVersion)} · {diagnostics.Platform}"),
                Fact($"{Strings.Home_Waiting}: {diagnostics.WaitingToSync}"),
                Fact($"{Strings.Home_NumbersLeft}: {diagnostics.NumbersLeft?.ToString() ?? "—"}"),
                Fact(diagnostics.LastSyncedAtUtc is { } synced
                    ? Language.Format(Strings.Home_LastSync, synced.ToLocalTime())
                    : Strings.Home_NeverSynced),
            },
        };

        BuildFor(host, Pages.Section.More,
            Heading(Strings.Problem_Title),
            Note(Strings.Problem_Note),
            Ui.Labeled(Strings.Problem_What, _what),
            Ui.CardOf(new VerticalStackLayout { Spacing = Space.Sm, Children = { Ui.Caption(Strings.Problem_Sends), facts } }),
            _send,
            Status,
            Busy);
    }

    private static Label Fact(string text) => Ui.Caption(text);

    private async Task SendAsync()
    {
        var sent = await _host.ReportProblemAsync(_what.Text);
        _what.Text = "";
        Status.Text = sent ? Strings.Problem_Sent : Strings.Problem_Saved;
    }
}
