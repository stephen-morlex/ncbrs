using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;

namespace NCBRS.Client.App.Pages;

/// <summary>Opens the store, then hands over to whichever stage the tablet is in.</summary>
public sealed class StartPage(DeviceHost host) : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Content = new ActivityIndicator { IsRunning = true, VerticalOptions = LayoutOptions.Center };
        await host.OpenAsync();
        Flow.Advance(host);
    }
}

/// <summary>
/// The store exists and cannot be read. Nothing is offered that could write
/// over it: it may hold births nobody has synced.
/// </summary>
public sealed class UnreadablePage : FlowPage
{
    public UnreadablePage(DeviceHost host) : base(Strings.Unreadable_Title)
        => Build(
            Heading(Strings.Unreadable_Heading),
            Note(Strings.Unreadable_Body),
            Note(host.UnreadableReason ?? ""));
}
