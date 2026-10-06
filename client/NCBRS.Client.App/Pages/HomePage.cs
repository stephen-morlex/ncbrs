using Microsoft.Maui.Controls.Shapes;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The home screen after unlock (the design handoff's mockup 2): who is
/// working and where, one card saying where the births stand with the Sync
/// button on it, registering a birth as the main action, the other tasks, and
/// the births registered most recently.
///
/// **What needs attention comes first.** A refused birth is one the registry
/// does not have, and a USB export is not a confirmation; both sit above
/// everything else, in words as well as colour, each a tap to its remedy.
/// </summary>
public sealed class HomePage : FlowPage
{
    private readonly DeviceHost _host;

    /// <param name="message">What the last sync from this screen did, shown on the sync card.</param>
    public HomePage(DeviceHost host, string? message = null) : base(Strings.Home_Title)
    {
        _host = host;
        var facility = host.Session?.Facility;
        var refused = facility?.Refused.Count ?? 0;
        var unconfirmed = host.ExportedAndUnconfirmed;
        var name = host.UnlockedAs?.DisplayName ?? "";

        var views = new List<View> { Header(host, name) };

        if (refused > 0)
        {
            views.Add(Ui.Notice(Icons.Error,
                refused == 1 ? Strings.Register_RefusedOne : Language.Format(Strings.Register_RefusedMany, refused),
                Tone.Danger, () => _ = Go(new RefusedPage(host))));
        }

        if (unconfirmed > 0)
        {
            views.Add(Ui.Notice(Icons.Usb,
                Language.Format(Strings.Register_ExportedUnconfirmed, unconfirmed, host.State.LastExport?.AtUtc.ToLocalTime()),
                Tone.Primary, () => _ = Go(new SyncPage(host))));
        }

        views.Add(SyncCard(message));
        views.Add(RegisterCard());
        views.Add(Ui.Group(Strings.Home_OtherTasks, Ui.Columns(2,
            Ui.Tile(Icons.ScanLine, Strings.Register_Check, null, () => _ = Go(new CheckCertificatePage(host))),
            Ui.Tile(Icons.Print, Strings.Register_PrintCertificate, null, () => _ = Go(new PrintCertificatePage(host))),
            Ui.Tile(Icons.Lock, Strings.Register_Lock, null, () =>
            {
                host.Lock();
                Flow.Advance(host);
            }, Ui.TextMuted),
            Ui.Tile(Icons.Export, Strings.Tile_UsbTitle, null, () => _ = Go(new SyncPage(host))))));

        var recent = TabletRecords.All(host).Take(3).ToList();
        if (recent.Count > 0)
        {
            views.Add(RecentBirths(recent));
        }

        BuildInside(host, Section.Home, [.. views]);
    }

    /// <summary>The greeting and the facility, with the language switch and who is signed in.</summary>
    private static View Header(DeviceHost host, string name)
    {
        var place = new HorizontalStackLayout
        {
            Spacing = Space.Xs,
            Children = { Ui.Icon(Icons.MapPin, Ui.TextMuted, 14), Ui.Subtitle(host.EnrolledTo) },
        };

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Md,
        };
        row.Add(new VerticalStackLayout
        {
            Spacing = Space.Xs,
            VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Title(Language.Format(Strings.Home_Greeting, FirstName(name))), place },
        }, 0);
        row.Add(Flow.LanguageSwitch(host), 1);
        var avatar = Ui.Avatar(name, strong: true);
        avatar.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetDescription(avatar, name);
        row.Add(avatar, 2);
        return row;
    }

    /// <summary>
    /// One card for where the births stand, in the order a registrar needs it:
    /// no signal (the births are safe here), births waiting, numbers running
    /// low, or everything in the registry, with when it was last reached.
    /// Sync runs the same sync as the Sync screen.
    /// </summary>
    private View SyncCard(string? message)
    {
        var facility = _host.Session?.Facility;
        var waiting = facility?.SendableCount ?? 0;
        var left = facility?.BlockRemaining ?? 0;
        var low = facility?.NeedsMoreNumbers == true;
        var online = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

        var (icon, colour, title) =
            !online ? (Icons.CloudOff, Ui.TextMuted, Strings.Home_Offline)
            : waiting > 0 ? (Icons.CloudUpload, Ui.Theme.PendingForeground, Language.Format(Strings.Home_WaitingTitle, waiting))
            : low ? (Icons.Warning, Ui.Theme.PendingForeground, Strings.Home_LowNumbers)
            : (Icons.CloudCheck, Ui.Primary, Strings.Home_Synced);
        var detail = !online ? Strings.Home_OfflineBody
            : _host.State.LastSyncedAtUtc is { } last ? Language.Format(Strings.Home_LastSync, last.ToLocalTime())
            : Strings.Home_NeverSynced;

        var sync = Ui.ActionChip(Icons.Sync, Strings.Nav_Sync, () => _ = RunAsync(SyncAsync));

        var top = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Md,
        };
        top.Add(Ui.IconBox(icon, colour), 0);
        var words = new VerticalStackLayout
        {
            Spacing = Space.Xs,
            VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Heading(title), Ui.Caption(detail) },
        };
        top.Add(words, 1);
        sync.VerticalOptions = LayoutOptions.Center;
        top.Add(sync, 2);

        var card = Ui.Card(
            top,
            Ui.Columns(2,
                Ui.Stat(waiting.ToString(Language.Current), Strings.Home_Waiting,
                    waiting > 0 ? Ui.Theme.PendingForeground : Ui.Primary, () => _ = Go(new SyncPage(_host))),
                Ui.Stat(left.ToString("N0", Language.Current), Strings.Home_NumbersLeft,
                    low ? Ui.Theme.PendingForeground : Ui.Primary)),
            Busy,
            Status);

        if (!string.IsNullOrWhiteSpace(message))
        {
            ((VerticalStackLayout)card.Content!).Add(Ui.Body(message));
        }

        return card;
    }

    private async Task SyncAsync()
    {
        var (report, staff) = await _host.SyncAsync();
        var message = string.Join("\n", new[] { SyncPage.Describe(report) }.Concat(report.Problems).Concat(staff));
        Flow.Show(new HomePage(_host, message));
    }

    /// <summary>The main action, as the largest thing on the screen: it works with no signal at all.</summary>
    private View RegisterCard()
    {
        var icon = new Border
        {
            BackgroundColor = Colors.White.WithAlpha(0.16f),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            WidthRequest = 48,
            HeightRequest = 48,
            Content = Ui.Icon(Icons.Add, Ui.OnPrimary, 26),
        };

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Md,
        };
        row.Add(icon, 0);
        row.Add(new VerticalStackLayout
        {
            Spacing = Space.Xs,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = Strings.Register_Title, FontSize = 20, FontFamily = Ui.SemiBold, TextColor = Ui.OnPrimary },
                new Label { Text = Strings.Home_RegisterHint, FontSize = 14, FontFamily = Ui.Regular, TextColor = Ui.OnPrimary.WithAlpha(0.85f) },
            },
        }, 1);
        row.Add(Ui.Icon(Icons.ChevronForward, Ui.OnPrimary, 22, directional: true), 2);

        var card = new Border
        {
            Content = row,
            BackgroundColor = Ui.Primary,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Card },
            Padding = Space.Lg,
            MinimumHeightRequest = 88,
        };
        SemanticProperties.SetDescription(card, $"{Strings.Register_Title}. {Strings.Home_RegisterHint}");
        Ui.Tappable(card, () => _ = Go(new RegisterPage(_host)));
        return card;
    }

    /// <summary>The newest few births on the tablet, as Records lists them, with the way to the rest.</summary>
    private View RecentBirths(IReadOnlyList<TabletRecord> recent)
    {
        var header = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        var label = Ui.SectionLabel(Strings.Home_RecentBirths);
        label.VerticalOptions = LayoutOptions.Center;
        header.Add(label, 0);
        header.Add(Ui.Link(Strings.Home_SeeAll, () => _ = Go(new RecordsPage(_host))), 1);

        return new VerticalStackLayout
        {
            Spacing = Space.Sm,
            Children = { header, Ui.ListCard([.. recent.Select(record => TabletRecords.Row(_host, record, Go))]) },
        };
    }

    /// <summary>"Hello, Alice", not "Hello, Alice Lado": a greeting, not a register entry.</summary>
    private static string FirstName(string name)
        => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
}
