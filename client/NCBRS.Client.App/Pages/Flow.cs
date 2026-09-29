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
        _ => new RegisterPage(host),
    };

    public static void Advance(DeviceHost host) => Show(For(host));

    /// <summary>
    /// A page within a stage (the refused births, a correction), shown in place,
    /// laid out in the current language's direction.
    /// </summary>
    public static void Show(Page page)
    {
        page.FlowDirection = AppLanguage.Direction;
        Application.Current!.Windows[0].Page = new NavigationPage(page) { FlowDirection = AppLanguage.Direction };
        AppLanguage.ApplyToWindow();
    }

    /// <summary>
    /// The English / العربية switch. It rebuilds the page the tablet is on, in
    /// the other language; <paramref name="confirmFirst"/> lets a page with
    /// something typed in it ask before that is cleared.
    /// </summary>
    public static Button LanguageSwitch(DeviceHost host, Func<Task<bool>>? confirmFirst = null)
    {
        var button = new Button
        {
            Text = Strings.Language_Switch,
            BackgroundColor = Colors.Transparent,
            TextColor = Color.FromArgb("#1B5E20"),
            BorderColor = Color.FromArgb("#1B5E20"),
            BorderWidth = 1,
            HorizontalOptions = LayoutOptions.End,
        };
        button.Clicked += async (_, _) =>
        {
            if (confirmFirst is not null && !await confirmFirst())
            {
                return;
            }

            // Posted, not run here. A culture set inside an async method lasts
            // only as long as that method: the UI thread's own culture reverts
            // when it returns, and controls that format themselves later (the
            // date picker) kept the old language while the text changed. Found
            // on the emulator: English labels over an Arabic date.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                AppLanguage.Toggle();
                Advance(host);
            });
        };
        return button;
    }
}

/// <summary>Shared layout for the flow's pages: a title, the content, and a line for what happened.</summary>
public abstract class FlowPage : ContentPage
{
    protected FlowPage(string title)
    {
        Title = title;
        Status = new Label { FontSize = 14, TextColor = Colors.DarkRed };
        Busy = new ActivityIndicator { IsVisible = false, IsRunning = false };
    }

    protected Label Status { get; }

    protected ActivityIndicator Busy { get; }

    protected ScrollView? Scroller { get; private set; }

    /// <summary>
    /// Lays the page out. The status line goes at the end unless the page places
    /// it itself: on a long form it belongs beside the button that produced it,
    /// or it lands below the fold and the tap appears to do nothing.
    /// </summary>
    protected void Build(params View[] views)
    {
        var stack = new VerticalStackLayout { Padding = 24, Spacing = 14 };
        foreach (var view in views)
        {
            stack.Add(view);
        }

        if (!views.Contains(Busy))
        {
            stack.Add(Busy);
        }

        if (!views.Contains(Status))
        {
            stack.Add(Status);
        }

        Content = Scroller = new ScrollView { Content = stack };
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

    protected static Label Heading(string text) => new() { Text = text, FontSize = 20, FontAttributes = FontAttributes.Bold };

    protected static Label Note(string text) => new() { Text = text, FontSize = 14 };

    protected static Entry Field(string placeholder, string? text = null, Keyboard? keyboard = null)
        => new() { Placeholder = placeholder, Text = text, Keyboard = keyboard ?? Keyboard.Default };
}
