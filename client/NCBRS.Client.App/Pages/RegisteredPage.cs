using Microsoft.Maui.Controls.Shapes;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Printing;
using NCBRS.Models;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// A birth just registered (the design handoff's mockup 6): its number, large
/// and in a face that keeps 0 and O apart, to write on the mother's card; what
/// was registered; and where the birth now is on its way to a certificate.
///
/// The number is a slip's number, not a certificate's. The tracker says so:
/// saved here, then in the registry at the next sync, and only then a
/// certificate the registry has signed. A provisional number says it is
/// provisional, loudly, because the registry will give the birth a real one.
/// </summary>
public sealed class RegisteredPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly PrintedDocument _slip;

    public RegisteredPage(DeviceHost host, RegistrationDraft registered, RegisterBirthRequest birth)
        : base(Strings.Registered_Title)
    {
        _host = host;
        _slip = host.SlipFor(registered, birth);

        var tick = new Border
        {
            BackgroundColor = Ui.PrimaryTint,
            StrokeThickness = 0,
            StrokeShape = new Ellipse(),
            WidthRequest = 72,
            HeightRequest = 72,
            HorizontalOptions = LayoutOptions.Center,
            Content = Ui.Icon(Icons.Tick, Ui.Primary, 36),
        };

        var title = Ui.Title(Strings.Registered_Title);
        title.HorizontalTextAlignment = TextAlignment.Center;
        var body = Ui.Subtitle(Strings.Registered_Body);
        body.HorizontalTextAlignment = TextAlignment.Center;

        var numberLabel = Ui.Caption(Strings.Registered_Number);
        numberLabel.HorizontalTextAlignment = TextAlignment.Center;
        var number = Ui.Number(registered.Brn, 24, Ui.Primary);
        number.HorizontalTextAlignment = TextAlignment.Center;
        var writeIt = Ui.Caption(Strings.Registered_WriteIt);
        writeIt.HorizontalTextAlignment = TextAlignment.Center;

        var views = new List<View>
        {
            new VerticalStackLayout { Spacing = Space.Md, Children = { tick, title, body } },
            Ui.Card(numberLabel, number, writeIt),
        };

        if (registered.IsProvisional)
        {
            views.Add(Ui.Notice(Icons.Warning, Language.Format(Strings.Register_Provisional, registered.Brn), Tone.Pending));
        }

        if (registered.BlockLow)
        {
            views.Add(Ui.Notice(Icons.Warning, Strings.Register_BlockLow, Tone.Pending));
        }

        var place = birth.PlaceOfBirthKind is { } kind ? Language.Name(kind) : "";
        views.Add(Ui.ListCard(
            Fact(Strings.Registered_Child, $"{BirthNames.Child(birth)} · {Language.Name(birth.Sex)}"),
            Fact(Strings.Registered_Born, $"{birth.DateOfBirth.ToString("d MMM yyyy", Language.Current)} · {place}")));

        views.Add(new VerticalStackLayout
        {
            Spacing = Space.Md,
            Children =
            {
                Stage(Strings.Registered_Saved, done: true, next: false),
                Stage(Strings.Registered_Reaches, done: false, next: true),
                Stage(Strings.Registered_Certificate, done: false, next: false),
            },
        });

        var sync = Ui.PrimaryButton(Strings.Register_Sync);
        sync.Clicked += async (_, _) => await RunAsync(SyncAsync);
        var another = Ui.SecondaryButton(Strings.Registered_Another);
        another.Clicked += (_, _) => Flow.Show(new RegisterPage(host));

        var footer = new VerticalStackLayout { Spacing = Space.Md, Children = { Status, Busy, sync, another } };
        if (host.Printer is not null)
        {
            var print = Ui.OutlineButton(Strings.Register_PrintSlip);
            print.Clicked += async (_, _) => await RunAsync(PrintAsync);
            footer.Add(print);
        }

        BuildFocused(Header(), footer, [.. views]);
    }

    /// <summary>A label and its value on one row.</summary>
    private static View Fact(string label, string value)
    {
        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = Space.Md,
            Padding = new Thickness(Space.Lg, Space.Md),
        };
        row.Add(Ui.Caption(label), 0);
        var shown = Ui.Heading(value);
        shown.FontSize = 15;
        shown.HorizontalTextAlignment = TextAlignment.End;
        row.Add(shown, 1);
        return row;
    }

    /// <summary>One stage on the way to a certificate: done (ticked), next (amber), or still to come.</summary>
    private static View Stage(string text, bool done, bool next)
    {
        var marker = new Border
        {
            WidthRequest = 28,
            HeightRequest = 28,
            StrokeShape = new Ellipse(),
            StrokeThickness = done ? 0 : 2,
            Stroke = next ? Ui.Pending : Ui.Theme.Ring,
            BackgroundColor = done ? Ui.Primary : next ? Ui.Theme.PendingBackground : Colors.Transparent,
            VerticalOptions = LayoutOptions.Center,
            Content = done ? Ui.Icon(Icons.Tick, Ui.OnPrimary, 16) : null,
        };

        var label = new Label
        {
            Text = text,
            FontSize = 15,
            FontFamily = done || next ? Ui.SemiBold : Ui.Regular,
            TextColor = done ? Ui.Text : next ? Ui.Theme.PendingForeground : Ui.TextMuted,
            VerticalOptions = LayoutOptions.Center,
        };

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = Space.Md,
        };
        row.Add(marker, 0);
        row.Add(label, 1);
        return row;
    }

    /// <summary>Close, back to the home screen.</summary>
    private View Header()
    {
        var close = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            WidthRequest = Ui.TouchTarget,
            HeightRequest = Ui.TouchTarget,
            HorizontalOptions = LayoutOptions.End,
            Content = Ui.Icon(Icons.Close, Ui.Text, 24),
        };
        SemanticProperties.SetDescription(close, Strings.Common_Close);
        Ui.Tappable(close, () => Flow.Show(new HomePage(_host)));
        return new ContentView { Padding = new Thickness(Space.Xl, Space.Sm, Space.Md, Space.Sm), Content = close };
    }

    private async Task SyncAsync()
    {
        var (report, staff) = await _host.SyncAsync();
        var message = string.Join("\n", new[] { SyncPage.Describe(report) }.Concat(report.Problems).Concat(staff));
        Flow.Show(new HomePage(_host, message));
    }

    private async Task PrintAsync()
    {
        if (_host.Printer is not null && await _host.Printer.PrintAsync(_slip) is { } problem)
        {
            await ShowProblemAsync(problem);
        }
    }
}
