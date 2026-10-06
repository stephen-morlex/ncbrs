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
            BarBackgroundColor = Ui.Primary,
        };
        AppLanguage.ApplyToWindow();
    }

    /// <summary>
    /// The English / العربية switch, named in the language it switches to. It
    /// rebuilds the page the tablet is on, in the other language;
    /// <paramref name="confirmFirst"/> lets a page with something typed in it
    /// ask before that is cleared.
    /// </summary>
    public static View LanguageSwitch(DeviceHost host, Func<Task<bool>>? confirmFirst = null)
    {
        var chip = Ui.ActionChip(Icons.Globe, Strings.Language_Switch, () => _ = SwitchAsync());
        chip.HorizontalOptions = LayoutOptions.End;
        chip.VerticalOptions = LayoutOptions.Center;
        return chip;

        async Task SwitchAsync()
        {
            if (confirmFirst is not null && !await confirmFirst())
            {
                return;
            }

            // Safe from inside an async handler because Language.Use changes the
            // process-wide defaults, not the async-local current culture.
            AppLanguage.Toggle();
            Advance(host);
        }
    }
}

/// <summary>The four places the bottom bar goes; everything else is under More.</summary>
public enum Section
{
    Home,
    Register,
    Records,
    More,
}

/// <summary>
/// Shared layout for the flow's pages.
///
/// Before the tablet is unlocked a page has the brand, its content and a line
/// saying which build this is, and nothing else: there is nowhere to go until
/// someone unlocks. After, every page sits above the same bottom bar, so a
/// registrar never has to hunt for the way back. Either way a page is one
/// vertical stack with 20 between its sections.
/// </summary>
public abstract class FlowPage : ContentPage
{
    protected FlowPage(string title)
    {
        Title = title;
        BackgroundColor = Ui.Background;
        Status = Ui.HideWhenEmpty(new Label { FontSize = 15, TextColor = Ui.Danger, FontFamily = Ui.SemiBold });
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
    /// Lays out a page before unlock: the brand with the language switch, the
    /// content, and the build. The status line goes at the end unless the page
    /// places it itself: on a long form it belongs beside the button that
    /// produced it, or it lands below the fold and the tap appears to do nothing.
    /// </summary>
    protected void Build(DeviceHost host, params View[] views)
        => Content = Body([Brand(host), .. views], Footer());

    /// <summary>
    /// A page reached both before unlock (from the unlock screen) and after: with
    /// the bottom bar once someone is working, standalone before.
    /// </summary>
    protected void BuildFor(DeviceHost host, Section section, params View[] views)
    {
        if (host.UnlockedAs is not null)
        {
            BuildInside(host, section, views);
        }
        else
        {
            Build(host, views);
        }
    }

    /// <summary>Lays out a page after unlock: its content, and the bottom bar.</summary>
    protected void BuildInside(DeviceHost host, Section section, params View[] views)
    {
        var grid = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
            BackgroundColor = Ui.Background,
        };
        grid.Add(Body(views), 0, 0);
        grid.Add(NavBar(host, section), 0, 1);
        Content = grid;
    }

    private ScrollView Body(IEnumerable<View> views, View? end = null)
    {
        var stack = new VerticalStackLayout
        {
            Padding = Space.Xl,
            Spacing = Space.Xl,
            MaximumWidthRequest = 840,
        };
        foreach (var view in views)
        {
            stack.Add(view);
        }

        // Unless the page placed them itself, at the top level or inside a
        // card. Added twice, Android refuses: a view has one parent.
        if (Busy.Parent is null)
        {
            stack.Add(Busy);
        }

        if (Status.Parent is null)
        {
            stack.Add(Status);
        }

        if (end is not null)
        {
            stack.Add(end);
        }

        return Scroller = new ScrollView { Content = stack };
    }

    /// <summary>The service's name and the Ministry's, with the language switch beside them.</summary>
    private static View Brand(DeviceHost host)
    {
        var logo = new Border
        {
            BackgroundColor = Ui.Primary,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            WidthRequest = 40,
            HeightRequest = 40,
            Content = Ui.Icon(Icons.Shield, Ui.OnPrimary, 22),
        };

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Md,
        };
        row.Add(logo, 0);
        row.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Heading(Strings.Brand_Name), Ui.Caption(Strings.Brand_Ministry) },
        }, 1);
        row.Add(Flow.LanguageSwitch(host), 2);
        return row;
    }

    /// <summary>Who made this, and which build: the first thing support asks.</summary>
    private static View Footer()
    {
        var line = Ui.Caption($"{Strings.Brand_Ministry} · {Language.Format(Strings.Footer_Version, AppInfo.Current.VersionString)}");
        line.HorizontalTextAlignment = TextAlignment.Center;
        return line;
    }

    /// <summary>
    /// Home, Register, Records and More, each an icon with its word. The
    /// current one is in the primary colour with a tinted pill behind its icon.
    /// </summary>
    private View NavBar(DeviceHost host, Section current)
    {
        var items = new (Section Section, string Icon, string Label, Func<Page> Page)[]
        {
            (Section.Home, Icons.Home, Strings.Nav_Home, () => new HomePage(host)),
            (Section.Register, Icons.Add, Strings.Nav_Register, () => new RegisterPage(host)),
            (Section.Records, Icons.Records, Strings.Nav_Records, () => new RecordsPage(host)),
            (Section.More, Icons.More, Strings.Nav_More, () => new MorePage(host)),
        };

        var grid = new Grid
        {
            BackgroundColor = Ui.Surface,
            Padding = new Thickness(Space.Sm, Space.Sm, Space.Sm, Space.Md),
            ColumnDefinitions = [.. items.Select(_ => new ColumnDefinition(GridLength.Star))],
        };

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var selected = item.Section == current;
            var colour = selected ? Ui.Primary : Ui.TextMuted;

            var pill = new Border
            {
                BackgroundColor = selected ? Ui.PrimaryTint : Colors.Transparent,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = Space.Lg },
                WidthRequest = 56,
                HeightRequest = 32,
                HorizontalOptions = LayoutOptions.Center,
                Content = Ui.Icon(item.Icon, colour, 22),
            };
            var cell = new VerticalStackLayout
            {
                Spacing = Space.Xs,
                MinimumHeightRequest = 56,
                Children =
                {
                    pill,
                    new Label
                    {
                        Text = item.Label, FontSize = 13, TextColor = colour, HorizontalTextAlignment = TextAlignment.Center,
                        FontFamily = selected ? Ui.SemiBold : Ui.Regular,
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

        return new VerticalStackLayout { Children = { Ui.Divider(), grid } };
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
