using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The home screen after unlock: who is working, where the births stand, and
/// every task as one large tile.
///
/// **What needs attention comes first.** A refused birth is one the registry
/// does not have; running out of numbers is the road to provisional slips. Both
/// sit above the tasks, in words as well as colour, and each is a tap to its
/// remedy.
/// </summary>
public sealed class HomePage : FlowPage
{
    public HomePage(DeviceHost host) : base(Strings.Home_Title)
    {
        var facility = host.Session?.Facility;
        var waiting = facility?.SendableCount ?? 0;
        var left = facility?.BlockRemaining ?? 0;
        var refused = facility?.Refused.Count ?? 0;
        var unconfirmed = host.ExportedAndUnconfirmed;

        var views = new List<View>
        {
            Ui.Title(Language.Format(Strings.Home_Greeting, host.UnlockedAs?.DisplayName)),
            Ui.Caption(host.EnrolledTo),
        };

        if (refused > 0)
        {
            views.Add(Ui.Notice(Icons.Error,
                refused == 1 ? Strings.Register_RefusedOne : Language.Format(Strings.Register_RefusedMany, refused),
                Ui.Danger, Ui.DangerSoft, () => _ = Go(new RefusedPage(host))));
        }

        if (facility?.NeedsMoreNumbers == true)
        {
            views.Add(Ui.Notice(Icons.Warning, Strings.Home_NeedNumbers, Ui.Warning, Ui.WarningSoft, () => _ = Go(new SyncPage(host))));
        }

        if (unconfirmed > 0)
        {
            views.Add(Ui.Notice(Icons.Usb,
                Language.Format(Strings.Register_ExportedUnconfirmed, unconfirmed, host.State.LastExport?.AtUtc.ToLocalTime()),
                Ui.Info, Ui.InfoSoft, () => _ = Go(new SyncPage(host))));
        }

        // The two figures a registrar is asked about, side by side.
        var stats = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 12 };
        stats.Add(Ui.Stat(waiting.ToString(Language.Current), Strings.Home_Waiting, waiting > 0 ? Ui.Warning : Ui.Primary, () => _ = Go(new SyncPage(host))), 0);
        stats.Add(Ui.Stat(left.ToString("N0", Language.Current), Strings.Home_NumbersLeft, facility?.NeedsMoreNumbers == true ? Ui.Warning : Ui.Primary), 1);
        views.Add(stats);

        if (waiting == 0 && refused == 0)
        {
            views.Add(Ui.Caption(Strings.Home_AllSent, Ui.Primary));
        }

        views.Add(Ui.Overline(Strings.Home_Tasks));
        views.Add(Tiles(
            Ui.Tile(Icons.Add, Strings.Register_Title, Strings.Tile_Register, () => _ = Go(new RegisterPage(host))),
            Ui.Tile(Icons.Sync, Strings.Register_Sync, Strings.Tile_Sync, () => _ = Go(new SyncPage(host)),
                badge: waiting > 0 ? waiting.ToString(Language.Current) : null),
            Ui.Tile(Icons.Verified, Strings.Register_Check, Strings.Tile_Check, () => _ = Go(new CheckCertificatePage(host))),
            Ui.Tile(Icons.Print, Strings.Register_PrintCertificate, Strings.Tile_Print, () => _ = Go(new PrintCertificatePage(host))),
            Ui.Tile(Icons.Usb, Strings.Tile_UsbTitle, Strings.Tile_Usb, () => _ = Go(new SyncPage(host)), Ui.Info),
            Ui.Tile(Icons.Lock, Strings.Register_Lock, Strings.Tile_Lock, () =>
            {
                host.Lock();
                Flow.Advance(host);
            }, Ui.TextMuted)));

        BuildInside(host, Section.Home, [.. views]);
    }

    /// <summary>Two tiles a row on a phone, three on a tablet held sideways.</summary>
    private static View Tiles(params View[] tiles)
    {
        var columns = DeviceDisplay.Current.MainDisplayInfo is { Width: > 0 } display
                      && display.Width / display.Density >= 720 ? 3 : 2;

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (var i = 0; i < tiles.Length; i++)
        {
            if (i % columns == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            grid.Add(tiles[i], i % columns, i / columns);
        }

        return grid;
    }
}
