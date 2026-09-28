using NCBRS.Client.Storage;
using NCBRS.Devices;
using NCBRS.Models;

namespace NCBRS.Client.App;

public partial class MainPage : ContentPage
{
    private EncryptedStateFile? _store;
    private DeviceState? _state;
    private DeviceSession? _session;

    public MainPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_session is not null)
        {
            return;
        }

        try
        {
            _store = await DeviceStorage.OpenAsync();
            _state = await _store.LoadAsync() ?? await DemoProvisionAsync(_store);
            _session = DeviceSession.Restore(_state);
            RefreshStatus();
        }
        catch (StateFileUnreadableException exception)
        {
            // Stop here. Carrying on as a fresh device would overwrite births
            // that were never synced.
            Register.IsEnabled = false;
            Result.Text = "This tablet's records cannot be opened. Do not reset the app; contact the district. "
                          + exception.Message;
        }
    }

    /// <summary>
    /// Demo only, until handover exists (B3): a device identity, a fresh key and
    /// a block, saved so they survive a restart like a real provisioned tablet.
    /// </summary>
    private static async Task<DeviceState> DemoProvisionAsync(EncryptedStateFile store)
    {
        var state = new DeviceState
        {
            Identity = new DeviceIdentity("DEMO-TABLET-01", Guid.Parse("0199c000-0000-7000-8000-00000000f004"),
                new Uri("http://localhost:5259/")),
            DevicePrivateKeyPem = DeviceSignature.GenerateKeyPair().PrivateKeyPem,
            Brn = new BrnState(200_000, 200_009, 200_000, 0, null, null),
        };
        await store.SaveAsync(state);
        return state;
    }

    private async void OnRegister(object? sender, EventArgs e)
    {
        if (_session is null || _store is null || _state is null)
        {
            return;
        }

        var name = ChildName.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Result.Text = "Enter the child's name first.";
            return;
        }

        // A date of birth is always sent: left unset it is DateTime.MinValue,
        // which the centre accepts as a date and is meaningless as one.
        var draft = _session.Facility.RegisterBirth(new RegisterBirthRequest
        {
            ChildFullName = name,
            DateOfBirth = DateTime.Today,
            Sex = Sex.Undetermined,
            RegisteredAtUtc = DateTime.UtcNow,
        });

        // Saved before anything is shown: a number on a slip the store does not
        // know about is a number the next registration can hand out again.
        await _store.SaveAsync(_session.Capture(_state));

        Result.Text = draft.IsProvisional
            ? $"Provisional slip issued: {draft.Brn} (a permanent BRN is assigned on sync)"
            : $"Registered — BRN {draft.Brn}";
        if (draft.BlockLow)
        {
            Result.Text += "  · block running low, sync to request more";
        }

        ChildName.Text = string.Empty;
        RefreshStatus();
    }

    private void RefreshStatus()
        => StatusLine.Text = $"Queued to sync: {_session!.Facility.PendingCount}   ·   block numbers left: {_session.Facility.BlockRemaining}";
}
