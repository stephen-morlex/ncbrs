using NCBRS.Client.App.Pages;
using NCBRS.Client.App.Services;

namespace NCBRS.Client.App;

public partial class App : Application
{
    private readonly DeviceHost _host;

    public App(DeviceHost host)
    {
        InitializeComponent();
        _host = host;

        // Before the first page is built, so it is built in the right language.
        AppLanguage.Apply(this);
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new NavigationPage(new StartPage(_host)) { Title = "NCBRS" });
}
