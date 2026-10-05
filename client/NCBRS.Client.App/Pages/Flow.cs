using Microsoft.Maui.Controls.Shapes;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The screen follows the tablet's stage, never a remembered navigation stack:
/// after every act the host is asked where the tablet now is.
/// </summary>
public static class Flow
{
    public static Page For(DeviceHost host) => host.Stage switch
    {
        Stage.Unreadable => new UnreadablePage(host),
        Stage.Handover => new HandoverPage(host),
        Stage.RegistrarSignIn => new RegistrarSignInPage(host),
        Stage.Provision => new ProvisionPage(host),
        Stage.Unlock => new UnlockPage(host),
        // Unlocked: the home screen, from which every task is a tap away.
        _ => new HomePage(host),
    };

    public static void Advance(DeviceHost host) => Show(For(host));

    /// <summary>
    /// A page within a stage (the refused births, a correction), shown in place,
    /// laid out in the current language's direction. Each page draws its own
    /// bars, so the system navigation bar is hidden.
    /// </summary>
    public static void Show(Page page)
    {
        page.FlowDirection = AppLanguage.Direction;
        NavigationPage.SetHasNavigationBar(page, false);
        Application.Current!.Windows[0].Page = new NavigationPage(page)
        {
            FlowDirection = AppLanguage.Direction,
            BarBackgroundColor = Ui.PrimaryDark,
        };
        AppLanguage.ApplyToWindow();
    }

    /// <summary>
    /// The English / العربية switch. It rebuilds the page the tablet is on, in
    /// the other language; <paramref name="confirmFirst"/> lets a page with
    /// something typed in it ask before that is cleared.
    /// </summary>
    public static Button LanguageSwitch(DeviceHost host, Func<Task<bool>>? confirmFirst = null, bool onDark = false)
    {
        var button = new Button
        {
            Text = Strings.Language_Switch,
            BackgroundColor = Colors.Transparent,
            TextColor = onDark ? Colors.White : Ui.Primary,
            BorderColor = onDark ? Colors.White.WithAlpha(0.7f) : Ui.Primary,
            BorderWidth = 1,
            CornerRadius = 20,
            FontSize = 15,
            MinimumHeightRequest = 40,
            Padding = new Thickness(14, 4),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
        };
        button.Clicked += async (_, _) =>
        {
            if (confirmFirst is not null && !await confirmFirst())
            {
                return;
            }

            // Safe from inside an async handler because Language.Use changes the
            // process-wide defaults, not the async-local current culture.
            AppLanguage.Toggle();
            Advance(host);
        };
        return button;
    }
}

/// <summary>The four places the bottom bar goes, and the menu with everything else.</summary>
public enum Section
{
    Home,
    Register,
    Sync,
    Check,
    More,
}

/// <summary>
/// Shared layout for the flow's pages.
///
/// Before the tablet is unlocked a page has a branded header and a footer and
/// nothing else: there is nowhere to go until someone signs in.
/// After, every page has the same frame -- a title bar, the content, a footer
/// saying where the births stand, and a bottom bar to every task -- so a
/// registrar never has to hunt for the way back.
/// </summary>
public abstract class FlowPage : ContentPage
{
    protected FlowPage(string title)
    {
        Title = title;
        BackgroundColor = Ui.Background;
        Status = Ui.HideWhenEmpty(new Label { FontSize = 15, TextColor = Ui.Danger, FontFamily = "OpenSansSemibold" });
        Busy = new ActivityIndicator { IsVisible = false, IsRunning = false, Color = Ui.Primary };
    }

    protected Label Status { get; }

    protected ActivityIndicator Busy { get; }

    protected ScrollView? Scroller { get; private set; }

    /// <summary>
    /// Whether the registrar may leave for another page. A page holding
    /// something typed asks first; leaving rebuilds the target and drops it.
    /// </summary>
    public virtual Task<bool> CanLeaveAsync() => Task.FromResult(true);

    /// <summary>
    /// Lays out a page before unlock: the brand, the content, a footer. The
    /// status line goes at the end unless the page places it itself: on a long
    /// form it belongs beside the button that produced it, or it lands below
    /// the fold and the tap appears to do nothing.
    /// </summary>
    protected void Build(params View[] views)
    {
        var header = new VerticalStackLayout
        {
            BackgroundColor = Ui.Primary,
            Padding = new Thickness(Ui.Gutter, 20, Ui.Gutter, 18),
            Spacing = 2,
            Children =
            {
                new Label { Text = Strings.Brand_Name, FontSize = 22, FontFamily = "OpenSansSemibold", TextColor = Colors.White },
                new Label { Text = Strings.Brand_Ministry, FontSize = 13, TextColor = Colors.White.WithAlpha(0.85f) },
            },
        };

        Content = Layout(header, Body(views), Footer());
    }

    /// <summary>
    /// A page reached both before unlock (from the unlock screen) and after: framed
    /// with the navigation once someone is working, standalone before.
    /// </summary>
    protected void BuildFor(DeviceHost host, Section section, params View[] views)
    {
        if (host.UnlockedAs is not null)
        {
            BuildInside(host, section, views);
        }
        else
        {
            Build(views);
        }
    }

    /// <summary>Lays out a page after unlock: title bar, content, status footer and bottom navigation.</summary>
    protected void BuildInside(DeviceHost host, Section section, params View[] views)
    {
        var bar = new Grid
        {
            BackgroundColor = Ui.Primary,
            Padding = new Thickness(Ui.Gutter, 12),
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            MinimumHeightRequest = 60,
        };
        bar.Add(new Label
        {
            Text = Title, FontSize = 20, FontFamily = "OpenSansSemibold", TextColor = Colors.White,
            VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation,
        }, 0);
        bar.Add(Flow.LanguageSwitch(host, CanLeaveAsync, onDark: true), 1);

        var bottom = new VerticalStackLayout { Children = { StatusStrip(host), NavBar(host, section) } };
        Content = Layout(bar, Body(views), bottom);
    }

    private static Grid Layout(View top, View middle, View end)
    {
        var grid = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
            BackgroundColor = Ui.Background,
        };
        grid.Add(top, 0, 0);
        grid.Add(middle, 0, 1);
        grid.Add(end, 0, 2);
        return grid;
    }

    private ScrollView Body(View[] views)
    {
        var stack = new VerticalStackLayout
        {
            Padding = new Thickness(Ui.Gutter, 20, Ui.Gutter, 24),
            Spacing = 14,
            MaximumWidthRequest = 840,
        };
        foreach (var view in views)
        {
            stack.Add(view);
        }

        // Unless the page placed them itself -- at the top level or inside a
        // card. Added twice, Android refuses: a view has one parent.
        if (Busy.Parent is null)
        {
            stack.Add(Busy);
        }

        if (Status.Parent is null)
        {
            stack.Add(Status);
        }

        return Scroller = new ScrollView { Content = stack };
    }

    /// <summary>Who made this, and which build: the first thing support asks.</summary>
    private static View Footer() => new VerticalStackLayout
    {
        Padding = new Thickness(Ui.Gutter, 10),
        BackgroundColor = Ui.Surface,
        Children =
        {
            new Label
            {
                Text = $"{Strings.Brand_Ministry} · {Language.Format(Strings.Footer_Version, AppInfo.Current.VersionString)}",
                FontSize = 12, TextColor = Ui.TextMuted, HorizontalTextAlignment = TextAlignment.Center,
            },
        },
    };

    /// <summary>
    /// Where the births stand, on every page: who is working, where, and what
    /// has not reached the registry. Tapping it goes to Sync. Amber when a
    /// birth was refused or numbers are running out -- and it says so in
    /// words, never by colour alone.
    /// </summary>
    private View StatusStrip(DeviceHost host)
    {
        _stripHost = host;
        _strip = new Grid
        {
            Padding = new Thickness(Ui.Gutter, 8),
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 8,
        };

        var who = new Label
        {
            Text = $"{host.EnrolledTo} · {host.UnlockedAs?.DisplayName}",
            FontSize = 13, FontFamily = "OpenSansSemibold", TextColor = Ui.Text, LineBreakMode = LineBreakMode.TailTruncation,
        };
        _stripWhere = new Label { FontSize = 13 };
        _stripIcon = new ContentView();
        _strip.Add(new VerticalStackLayout { Spacing = 1, Children = { who, _stripWhere } }, 0);
        _strip.Add(_stripIcon, 1);

        Ui.Tappable(_strip, () =>
            _ = Go((host.Session?.Facility?.Refused.Count ?? 0) > 0 ? new RefusedPage(host) : new SyncPage(host)));
        RefreshStatus();
        return _strip;
    }

    private DeviceHost? _stripHost;
    private Grid? _strip;
    private Label? _stripWhere;
    private ContentView? _stripIcon;

    /// <summary>Re-reads where the births stand, after a registration or a sync on this page.</summary>
    protected void RefreshStatus()
    {
        if (_stripHost is null || _strip is null || _stripWhere is null || _stripIcon is null)
        {
            return;
        }

        var facility = _stripHost.Session?.Facility;
        var refused = facility?.Refused.Count ?? 0;
        var attention = refused > 0 || facility?.NeedsMoreNumbers == true;

        _strip.BackgroundColor = attention ? Ui.WarningSoft : Ui.PrimarySoft;
        _stripWhere.Text = refused > 0
            ? (refused == 1 ? Strings.Register_RefusedOne : Language.Format(Strings.Register_RefusedMany, refused))
            : Language.Format(Strings.Footer_Status, facility?.SendableCount ?? 0, facility?.BlockRemaining ?? 0);
        _stripWhere.TextColor = attention ? Ui.Warning : Ui.TextMuted;
        _stripIcon.Content = Ui.Icon(attention ? Icons.Warning : Icons.Sync, attention ? Ui.Warning : Ui.Primary, 20);
    }

    private View NavBar(DeviceHost host, Section current)
    {
        var items = new (Section Section, string Icon, string Label, Func<Page> Page)[]
        {
            (Section.Home, Icons.Home, Strings.Nav_Home, () => new HomePage(host)),
            (Section.Register, Icons.Add, Strings.Nav_Register, () => new RegisterPage(host)),
            (Section.Sync, Icons.Sync, Strings.Nav_Sync, () => new SyncPage(host)),
            (Section.Check, Icons.Verified, Strings.Nav_Check, () => new CheckCertificatePage(host)),
            (Section.More, Icons.More, Strings.Nav_More, () => new MorePage(host)),
        };

        var grid = new Grid
        {
            BackgroundColor = Ui.Surface,
            Padding = new Thickness(4, 6, 4, 8),
            ColumnDefinitions = [.. items.Select(_ => new ColumnDefinition(GridLength.Star))],
        };

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var selected = item.Section == current;
            var colour = selected ? Ui.Primary : Ui.TextMuted;

            var pill = new Border
            {
                BackgroundColor = selected ? Ui.PrimarySoft : Colors.Transparent,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 16 },
                Padding = new Thickness(16, 4),
                HorizontalOptions = LayoutOptions.Center,
                Content = Ui.Icon(item.Icon, colour, 24),
            };
            var cell = new VerticalStackLayout
            {
                Spacing = 4,
                MinimumHeightRequest = Ui.TouchTarget,
                Children =
                {
                    pill,
                    new Label
                    {
                        Text = item.Label, FontSize = 12, TextColor = colour, HorizontalTextAlignment = TextAlignment.Center,
                        FontFamily = selected ? "OpenSansSemibold" : "OpenSansRegular",
                    },
                },
            };
            SemanticProperties.SetDescription(cell, item.Label);
            if (!selected)
            {
                Ui.Tappable(cell, () => _ = Go(item.Page()));
            }

            grid.Add(cell, i);
        }

        return grid;
    }

    /// <summary>Go to another page, asking first if this one holds something typed.</summary>
    protected async Task Go(Page page)
    {
        if (await CanLeaveAsync())
        {
            Flow.Show(page);
        }
    }

    /// <summary>Show a problem where the registrar is looking: set it, and bring it into view.</summary>
    protected async Task ShowProblemAsync(string text)
    {
        Status.Text = text;
        if (Scroller is not null)
        {
            await Scroller.ScrollToAsync(Status, ScrollToPosition.Center, animated: true);
        }
    }

    /// <summary>Run an act with the page busy, showing a failure rather than crashing the app.</summary>
    protected async Task RunAsync(Func<Task> act)
    {
        Busy.IsVisible = Busy.IsRunning = true;
        Status.Text = "";
        try
        {
            await act();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Status.Text = exception.Message;
        }
        finally
        {
            Busy.IsVisible = Busy.IsRunning = false;
        }
    }

    protected static Label Heading(string text) => Ui.Title(text);

    protected static Label Note(string text) => Ui.Body(text, Ui.TextMuted);

    protected static Entry Field(string placeholder, string? text = null, Keyboard? keyboard = null)
        => new() { Placeholder = placeholder, Text = text, Keyboard = keyboard ?? Keyboard.Default };
}
