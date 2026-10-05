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
        var tasks = Group(
            Ui.MenuRow(Icons.Add, Strings.Register_Title, Strings.Tile_Register, () => _ = Go(new RegisterPage(host))),
            Ui.MenuRow(Icons.Sync, Strings.Register_Sync, Strings.Tile_Sync, () => _ = Go(new SyncPage(host))),
            Ui.MenuRow(Icons.Usb, Strings.Tile_UsbTitle, Strings.Tile_Usb, () => _ = Go(new SyncPage(host)), Ui.Info),
            Ui.MenuRow(Icons.Error, Strings.Refused_Heading, null, () => _ = Go(new RefusedPage(host)), Ui.Danger),
            Ui.MenuRow(Icons.Verified, Strings.Register_Check, Strings.Tile_Check, () => _ = Go(new CheckCertificatePage(host))),
            Ui.MenuRow(Icons.Print, Strings.Register_PrintCertificate, Strings.Tile_Print, () => _ = Go(new PrintCertificatePage(host))),
            Ui.MenuRow(Icons.Print, Strings.Printer_Title, Strings.Tile_Printer, () => _ = Go(new PrinterPage(host))));

        var tablet = Group(
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

        BuildInside(host, Section.More,
            Ui.Card(
                new HorizontalStackLayout
                {
                    Spacing = 12,
                    Children =
                    {
                        Ui.Icon(Icons.Person, Ui.Primary, 32),
                        new VerticalStackLayout
                        {
                            VerticalOptions = LayoutOptions.Center,
                            Children =
                            {
                                Ui.Heading(host.UnlockedAs?.DisplayName ?? ""),
                                Ui.Caption(host.EnrolledTo),
                            },
                        },
                    },
                }),
            Ui.Overline(Strings.More_Tasks), tasks,
            Ui.Overline(Strings.More_Tablet), tablet);
    }

    /// <summary>Rows in one rounded card, with hairlines between them.</summary>
    private static View Group(params View[] rows)
    {
        var stack = new VerticalStackLayout();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                stack.Add(new BoxView { HeightRequest = 1, Color = Ui.Border, Margin = new Thickness(68, 0, 0, 0) });
            }

            stack.Add(rows[i]);
        }

        return new Border
        {
            Content = stack,
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Border,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            Padding = 0,
        };
    }
}
