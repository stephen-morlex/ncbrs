using NCBRS.Client.App.Services;

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

    public static void Advance(DeviceHost host)
        => Application.Current!.Windows[0].Page = new NavigationPage(For(host));
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

    protected void Build(params View[] views)
    {
        var stack = new VerticalStackLayout { Padding = 24, Spacing = 14 };
        foreach (var view in views)
        {
            stack.Add(view);
        }

        stack.Add(Busy);
        stack.Add(Status);
        Content = new ScrollView { Content = stack };
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
