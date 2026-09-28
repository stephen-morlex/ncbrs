using NCBRS.Client.App.Services;
using NCBRS.Client.Auth;
using NCBRS.Client.Network;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Handover: a district officer signs in on the tablet, picks the facility, and
/// enrols it. The officer's session is ended as soon as the tablet is enrolled.
/// </summary>
public sealed class HandoverPage : FlowPage
{
#if DEBUG
    private const string DefaultCentre = "http://localhost:5259/";
    private const string DefaultRealm = "http://localhost:8080/realms/ncbrs/";
#else
    private const string DefaultCentre = "";
    private const string DefaultRealm = "";
#endif

    private readonly DeviceHost _host;
    private readonly Entry _centre = Field("Registry address", DefaultCentre, Keyboard.Url);
    private readonly Entry _realm = Field("Sign-in address", DefaultRealm, Keyboard.Url);
    private readonly Entry _district = Field("District node address (optional)", keyboard: Keyboard.Url);
    private readonly Entry _label = Field("Label, e.g. Maternity ward tablet (optional)");
    private readonly Picker _facility = new() { Title = "Facility", IsVisible = false, ItemDisplayBinding = new Binding(nameof(FacilitySummary.Name)) };
    private readonly Button _signIn = new() { Text = "District officer: sign in" };
    private readonly Button _enrol = new() { Text = "Enrol this tablet", IsVisible = false };
    private InteractiveSignIn? _officer;

    public HandoverPage(DeviceHost host) : base("Hand over this tablet")
    {
        _host = host;
        _signIn.Clicked += async (_, _) => await RunAsync(SignInAsync);
        _enrol.Clicked += async (_, _) => await RunAsync(EnrolAsync);
        Build(
            Heading("Hand over this tablet"),
            Note("A district officer enrols the tablet to its facility. The officer's sign-in ends as soon as it is done."),
            _centre, _realm, _district, _label, _signIn, _facility, _enrol);
    }

    private static Uri? AddressOf(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return Uri.TryCreate(value.EndsWith('/') ? value : value + "/", UriKind.Absolute, out var uri) ? uri : null;
    }

    private bool TryAddresses(out Uri centre, out Uri realm, out Uri? district)
    {
        centre = AddressOf(_centre.Text)!;
        realm = AddressOf(_realm.Text)!;
        district = AddressOf(_district.Text);

        if (centre is null || realm is null)
        {
            Status.Text = "Enter the registry and sign-in addresses.";
            return false;
        }

        if (district is null && !string.IsNullOrWhiteSpace(_district.Text))
        {
            Status.Text = "The district node address is not an address.";
            return false;
        }

        return true;
    }

    private async Task SignInAsync()
    {
        if (!TryAddresses(out var centre, out var realm, out _))
        {
            return;
        }

        var (officer, problem) = await _host.OfficerSignInAsync(realm);
        if (officer is null)
        {
            Status.Text = problem;
            return;
        }

        var facilities = await _host.ListFacilitiesAsync(officer, centre);
        if (facilities.Value is not { Count: > 0 } list)
        {
            Status.Text = facilities.Succeeded
                ? "This account may not hand tablets over to any facility."
                : DeviceHost.Describe("The facilities could not be listed", facilities);
            await officer.EndAsync();
            return;
        }

        _officer = officer;
        _facility.ItemsSource = list.ToList();
        _facility.SelectedIndex = list.Count == 1 ? 0 : -1;
        _facility.IsVisible = _enrol.IsVisible = true;
        _signIn.IsVisible = false;
    }

    private async Task EnrolAsync()
    {
        if (_officer is null || _facility.SelectedItem is not FacilitySummary facility)
        {
            Status.Text = "Choose the facility this tablet is for.";
            return;
        }

        if (!TryAddresses(out var centre, out var realm, out var district))
        {
            return;
        }

        // Named back before anything is sent: a tablet enrolled to the wrong
        // facility can only be put right by revoking it and starting again.
        if (!await DisplayAlertAsync(
                "Enrol this tablet?",
                $"It will register births for {facility.Name} ({facility.CountyCode}) and nowhere else.",
                "Enrol", "Cancel"))
        {
            return;
        }

        var officer = _officer;
        _officer = null;
        var result = await _host.EnrolAsync(officer, facility, centre, realm, district, _label.Text);
        if (!result.Enrolled)
        {
            Status.Text = result.Problem;
            _signIn.IsVisible = true;
            _facility.IsVisible = _enrol.IsVisible = false;
            return;
        }

        if (result.Rekeyed)
        {
            // Said out loud: the officer should know the tablet is a new device
            // at the registry, and why.
            await DisplayAlertAsync(
                "Enrolled under a new identity",
                $"This tablet's earlier identity was withdrawn at the registry, so it was enrolled to {facility.Name} "
                + $"as {_host.State.Identity!.DeviceId}.",
                "OK");
        }

        Flow.Advance(_host);
    }
}
