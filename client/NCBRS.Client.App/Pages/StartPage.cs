using NCBRS.Client.App.Services;

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
    public UnreadablePage(DeviceHost host) : base("Records cannot be opened")
        => Build(
            Heading("This tablet's records cannot be opened"),
            Note("Do not reset or reinstall the app: it may hold births that have not been sent. "
                 + "Contact the district office."),
            Note(host.UnreadableReason ?? ""));
}
