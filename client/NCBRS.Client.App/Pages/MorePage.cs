using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Everything the bottom bar has no room for, as one list: the tasks that are
/// not daily, the language, and locking the tablet. Nothing the app does is
/// reachable only from somewhere else.
/// </summary>
public sealed class MorePage : FlowPage
{
    public MorePage(DeviceHost host) : base(Strings.More_Title)
    {
        var name = host.UnlockedAs?.DisplayName ?? "";
        var person = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = Space.Md,
        };
        person.Add(Ui.Avatar(name, 48), 0);
        person.Add(new VerticalStackLayout
        {
            Spacing = Space.Xs,
            VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Heading(name), Ui.Caption(host.EnrolledTo) },
        }, 1);
        var who = Ui.CardOf(person);

        var tasks = Ui.ListCard(
            Ui.MenuRow(Icons.Sync, Strings.Register_Sync, Strings.Tile_Sync, () => _ = Go(new SyncPage(host))),
            Ui.MenuRow(Icons.Export, Strings.Tile_UsbTitle, Strings.Tile_Usb, () => _ = Go(new SyncPage(host))),
            Ui.MenuRow(Icons.Error, Strings.Refused_Heading, null, () => _ = Go(new RefusedPage(host)), Ui.Danger),
            Ui.MenuRow(Icons.ScanLine, Strings.Register_Check, Strings.Tile_Check, () => _ = Go(new CheckCertificatePage(host))),
            Ui.MenuRow(Icons.Print, Strings.Register_PrintCertificate, Strings.Tile_Print, () => _ = Go(new PrintCertificatePage(host))),
            Ui.MenuRow(Icons.Print, Strings.Printer_Title, Strings.Tile_Printer, () => _ = Go(new PrinterPage(host))));

        var tablet = Ui.ListCard(
            Ui.MenuRow(Icons.Language, Strings.More_Language, Strings.Language_Switch, () =>
            {
                AppLanguage.Toggle();
                Flow.Show(new MorePage(host));
            }),
            Ui.MenuRow(Icons.Lock, Strings.Register_Lock, Strings.Tile_Lock, () =>
            {
                host.Lock();
                Flow.Advance(host);
            }, Ui.TextMuted));

        var version = Ui.Caption($"{Strings.Brand_Ministry} · {Language.Format(Strings.Footer_Version, AppInfo.Current.VersionString)}");
        version.HorizontalTextAlignment = TextAlignment.Center;

        BuildInside(host, Section.More,
            Ui.Title(Strings.More_Title),
            who,
            Ui.Group(Strings.More_Tasks, tasks),
            Ui.Group(Strings.More_Tablet, tablet),
            version);
    }
}
