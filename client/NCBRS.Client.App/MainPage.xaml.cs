using NCBRS.Client;
using NCBRS.Client.Brn;
using NCBRS.Client.Sync;
using NCBRS.Models;

namespace NCBRS.Client.App;

public partial class MainPage : ContentPage
{
    private readonly FacilityClient _client;

    public MainPage()
    {
        InitializeComponent();

        // Illustrative session only. In the real shell these are restored from
        // the encrypted local store (B2) after the registrar unlocks (B3): the
        // persisted device private key, the granted BRN block with its saved
        // cursor, and the persisted outbox.
        const string deviceId = "DEMO-TABLET-01";
        var facilityId = Guid.Parse("0199c000-0000-7000-8000-00000000f004");
        _client = new FacilityClient(
            deviceId,
            facilityId,
            new DeviceBrnAllocator(deviceId, blockStart: 200_000, blockEnd: 200_009),
            new SyncOutbox(deviceId, facilityId),
            DeviceSigner.Generate());

        RefreshStatus();
    }

    private void OnRegister(object? sender, EventArgs e)
    {
        var name = ChildName.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Result.Text = "Enter the child's name first.";
            return;
        }

        var draft = _client.RegisterBirth(new RegisterBirthRequest
        {
            ChildFullName = name,
            Sex = Sex.Undetermined,
        });

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
        => StatusLine.Text = $"Queued to sync: {_client.PendingCount}   ·   block numbers left: {_client.BlockRemaining}";
}
