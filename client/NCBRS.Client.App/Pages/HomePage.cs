using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The home screen after unlock: who is working, where the births stand, and
/// every task as one tile.
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
        var name = host.UnlockedAs?.DisplayName ?? "";

        var views = new List<View> { Header(host, name) };

        if (refused > 0)
        {
            views.Add(Ui.Notice(Icons.Error,
                refused == 1 ? Strings.Register_RefusedOne : Language.Format(Strings.Register_RefusedMany, refused),
                Tone.Danger, () => _ = Go(new RefusedPage(host))));
        }

        if (facility?.NeedsMoreNumbers == true)
        {
            views.Add(Ui.Notice(Icons.Warning, Strings.Home_NeedNumbers, Tone.Pending, () => _ = Go(new SyncPage(host))));
        }

        if (unconfirmed > 0)
        {
            views.Add(Ui.Notice(Icons.Usb,
                Language.Format(Strings.Register_ExportedUnconfirmed, unconfirmed, host.State.LastExport?.AtUtc.ToLocalTime()),
                Tone.Primary, () => _ = Go(new SyncPage(host))));
        }

        // The two figures a registrar is asked about, side by side.
        var figures = new VerticalStackLayout
        {
            Spacing = Space.Sm,
            Children =
            {
                Ui.Columns(2,
                    Ui.Stat(waiting.ToString(Language.Current), Strings.Home_Waiting,
                        waiting > 0 ? Ui.Theme.PendingForeground : Ui.Primary, () => _ = Go(new SyncPage(host))),
                    Ui.Stat(left.ToString("N0", Language.Current), Strings.Home_NumbersLeft,
                        facility?.NeedsMoreNumbers == true ? Ui.Theme.PendingForeground : Ui.Primary)),
            },
        };
        if (waiting == 0 && refused == 0)
        {
            figures.Add(Ui.Caption(Strings.Home_AllSent));
        }

        views.Add(figures);

        views.Add(Ui.Group(Strings.Home_Tasks, Ui.Columns(Columns(),
            Ui.Tile(Icons.Add, Strings.Register_Title, null, () => _ = Go(new RegisterPage(host))),
            Ui.Tile(Icons.Sync, Strings.Register_Sync, null, () => _ = Go(new SyncPage(host)),
                badge: waiting > 0 ? waiting.ToString(Language.Current) : null),
            Ui.Tile(Icons.ScanLine, Strings.Register_Check, null, () => _ = Go(new CheckCertificatePage(host))),
            Ui.Tile(Icons.Print, Strings.Register_PrintCertificate, null, () => _ = Go(new PrintCertificatePage(host))),
            Ui.Tile(Icons.Export, Strings.Tile_UsbTitle, null, () => _ = Go(new SyncPage(host))),
            Ui.Tile(Icons.Lock, Strings.Register_Lock, null, () =>
            {
                host.Lock();
                Flow.Advance(host);
            }, Ui.TextMuted))));

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

    /// <summary>"Hello, Alice", not "Hello, Alice Lado": a greeting, not a register entry.</summary>
    private static string FirstName(string name)
        => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    /// <summary>Two tiles a row on a phone, three on a tablet held sideways.</summary>
    private static int Columns()
        => DeviceDisplay.Current.MainDisplayInfo is { Width: > 0 } display
           && display.Width / display.Density >= 720 ? 3 : 2;
}
