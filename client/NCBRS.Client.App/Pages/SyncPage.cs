using System.Globalization;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Network;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Getting births to the registry: by signal, or on a stick when there is none.
/// What is waiting, what was refused and what the last export left
/// unconfirmed are all here, so a registrar can see at a glance whether the
/// tablet holds anything the registry does not.
/// </summary>
public sealed class SyncPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly Label _result = Ui.HideWhenEmpty(Ui.Body(""));
    private readonly Label _waiting = new() { FontSize = 22, FontFamily = Ui.SemiBold, TextColor = Ui.Primary };
    private readonly Label _left = new() { FontSize = 22, FontFamily = Ui.SemiBold, TextColor = Ui.Primary };
    private readonly Border _refused;
    private readonly Border _exported;
    private readonly Label _refusedText;
    private readonly Label _exportedText;

    public SyncPage(DeviceHost host) : base(Strings.Nav_Sync)
    {
        _host = host;

        var sync = Ui.PrimaryButton(Strings.Register_Sync);
        sync.Clicked += async (_, _) => await RunAsync(SyncAsync);

        var export = Ui.SecondaryButton(Strings.Tile_UsbTitle);
        export.Clicked += async (_, _) => await RunAsync(ExportAsync);

        _refused = Ui.Notice(Icons.Error, "", Tone.Danger, () => _ = Go(new RefusedPage(host)));
        _refusedText = Ui.NoticeText(_refused);
        _exported = Ui.Notice(Icons.Usb, "", Tone.Primary);
        _exportedText = Ui.NoticeText(_exported);

        var figures = Ui.Columns(2,
            Figure(_waiting, Strings.Home_Waiting),
            Figure(_left, Strings.Home_NumbersLeft));

        BuildInside(host, Section.Home,
            Ui.Title(Strings.Nav_Sync),
            _refused,
            Ui.Card(Ui.Heading(Strings.Sync_Heading), Ui.Body(Strings.Sync_Intro, Ui.TextMuted), figures, sync, Busy, _result, Status),
            Ui.Card(
                new HorizontalStackLayout { Spacing = Space.Md, Children = { Ui.IconBox(Icons.Export), Ui.Heading(Strings.Sync_NoSignal) } },
                Ui.Body(Strings.Sync_NoSignalBody, Ui.TextMuted),
                export,
                _exported));

        Refresh();
    }

    /// <summary>A figure in a grey box, its value kept to update after a sync.</summary>
    private static Border Figure(Label value, string label) => new()
    {
        BackgroundColor = Ui.Muted,
        StrokeThickness = 0,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = Radius.Base },
        Padding = Space.Md,
        Content = new VerticalStackLayout { Spacing = Space.Xs, Children = { value, Ui.Caption(label) } },
    };

    private void Refresh()
    {
        var facility = _host.Session?.Facility;
        _waiting.Text = (facility?.SendableCount ?? 0).ToString(Language.Current);
        _left.Text = (facility?.BlockRemaining ?? 0).ToString("N0", Language.Current);
        _left.TextColor = facility?.NeedsMoreNumbers == true ? Ui.Theme.PendingForeground : Ui.Primary;

        // Never out of sight: a refused birth is one the registry does not have.
        var refused = facility?.Refused.Count ?? 0;
        _refused.IsVisible = refused > 0;
        _refusedText.Text = refused == 1 ? Strings.Register_RefusedOne : Language.Format(Strings.Register_RefusedMany, refused);

        // A stick is not a confirmation: say how many on the last one the
        // registry has not yet confirmed, so nobody takes it for registered.
        var unconfirmed = _host.ExportedAndUnconfirmed;
        _exported.IsVisible = unconfirmed > 0;
        _exportedText.Text = Language.Format(Strings.Register_ExportedUnconfirmed, unconfirmed, _host.State.LastExport?.AtUtc.ToLocalTime());
    }

    private async Task SyncAsync()
    {
        var (report, staff) = await _host.SyncAsync();
        _result.Text = Describe(report);
        Status.Text = string.Join("\n", report.Problems.Concat(staff));
        Refresh();
    }

    /// <summary>
    /// Seal what is waiting to sync and hand it to the share sheet, from where
    /// the registrar saves it to a USB stick or card. Sealed to the registry:
    /// whoever carries or finds the stick reads nobody's details.
    /// </summary>
    private async Task ExportAsync()
    {
        var (path, count, problem) = await _host.ExportAsync();
        if (problem is not null)
        {
            await ShowProblemAsync(problem);
            return;
        }

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = Language.Format(Strings.Export_ShareTitle, count),
            File = new ShareFile(path!, "application/json"),
        });

        _result.Text = Language.Format(Strings.Export_Done, count);
        Refresh();
    }

    /// <summary>
    /// Held is never shown as confirmed: a District node has the births, the
    /// registry has not seen them, and a family must not be told otherwise.
    /// </summary>
    internal static string Describe(WindowReport report)
    {
        var settled = report.Settlements.SelectMany(settlement => settlement.Settled).ToList();
        var rejected = report.Settlements.SelectMany(settlement => settlement.Rejected).ToList();
        var assigned = string.Concat(settled
            .Where(outcome => outcome.AssignedBrn is not null)
            .Select(outcome => "\n" + Language.Format(Strings.Sync_Assigned, outcome.Brn, outcome.AssignedBrn)));
        var refusals = string.Concat(rejected.Select(outcome =>
            "\n" + Language.Format(Strings.Sync_Refused, outcome.Brn,
                string.Join("; ", outcome.Errors?.Select(error => error.Message) ?? []))));

        var upload = report.Upload switch
        {
            null => Strings.Sync_Nothing,
            CentralOutcome.Succeeded => Language.Format(Strings.Sync_Sent, settled.Count)
                                        + (rejected.Count > 0 ? Language.Format(Strings.Sync_SentRefused, rejected.Count) : "")
                                        + assigned + refusals,
            CentralOutcome.Held => Strings.Sync_Held,
            CentralOutcome.Unauthorized => Strings.Sync_Unauthorized,
            CentralOutcome.Unreachable => Strings.Sync_Unreachable,
            _ => Language.Format(Strings.Sync_Other, Language.Name(report.Upload.Value)),
        };

        return upload + (report.BlockGranted is { } block
            // As they will be written, composed by the registry; a numeric
            // block (or a registry from before the format) has the numbers.
            ? "\n" + Language.Format(Strings.Sync_NewNumbers,
                block.FirstBrn ?? block.BlockStart.ToString(CultureInfo.InvariantCulture),
                block.LastBrn ?? block.BlockEnd.ToString(CultureInfo.InvariantCulture))
            : "");
    }
}
