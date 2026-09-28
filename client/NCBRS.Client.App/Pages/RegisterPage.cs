using NCBRS.Client.App.Services;
using NCBRS.Client.Network;
using NCBRS.Models;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The unlocked tablet: register a birth and sync. The form is a placeholder
/// until the first cut of the guided form (B4); what matters here is that every
/// act goes through the core and is saved before its result is shown.
/// </summary>
public sealed class RegisterPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly Label _queue = new() { FontSize = 13 };

    public RegisterPage(DeviceHost host) : base("Register a birth")
    {
        _host = host;
        var name = Field("Child's full name");
        var register = new Button { Text = "Register" };
        var sync = new Button { Text = "Sync now" };
        var lockTablet = new Button { Text = "Lock", BackgroundColor = Colors.Gray };
        var result = new Label { FontSize = 16 };

        register.Clicked += async (_, _) => await RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                Status.Text = "Enter the child's name first.";
                return;
            }

            var draft = await host.RegisterAsync(new RegisterBirthRequest
            {
                ChildFullName = name.Text.Trim(),
                DateOfBirth = DateTime.Today,
                Sex = Sex.Undetermined,
                RegisteredAtUtc = DateTime.UtcNow,
            });
            result.Text = draft.IsProvisional
                ? $"PROVISIONAL slip: {draft.Brn}. A permanent number is given when the tablet syncs."
                : $"Registered — BRN {draft.Brn}";
            if (draft.BlockLow)
            {
                result.Text += "\nNumbers are running low: sync when there is signal.";
            }

            name.Text = "";
            Refresh();
        });

        sync.Clicked += async (_, _) => await RunAsync(async () =>
        {
            var (report, staff) = await host.SyncAsync();
            result.Text = Describe(report);
            Status.Text = string.Join("\n", report.Problems.Concat(staff));
            Refresh();
        });

        lockTablet.Clicked += (_, _) =>
        {
            host.Lock();
            Flow.Advance(host);
        };

        Build(
            Heading($"Unlocked: {host.UnlockedAs?.DisplayName}"),
            Note("Placeholder form until the guided registration form (B4)."),
            name, register, _queue, sync, lockTablet, result);
        Refresh();
    }

    private void Refresh()
        => _queue.Text = $"Waiting to sync: {_host.Session?.Facility.PendingCount}   ·   numbers left: {_host.Session?.Facility.BlockRemaining}";

    /// <summary>
    /// Held is never shown as confirmed: a District node has the births, the
    /// registry has not seen them, and a family must not be told otherwise.
    /// </summary>
    private static string Describe(WindowReport report)
    {
        var settled = report.Settlements.SelectMany(settlement => settlement.Settled).ToList();
        var rejected = report.Settlements.SelectMany(settlement => settlement.Rejected).Count();
        var assigned = string.Concat(settled
            .Where(outcome => outcome.AssignedBrn is not null)
            .Select(outcome => $"\n{outcome.Brn} is now BRN {outcome.AssignedBrn}"));

        var upload = report.Upload switch
        {
            null => "Nothing waiting to send.",
            CentralOutcome.Succeeded => $"Sent: {settled.Count} registered with the registry"
                                        + (rejected > 0 ? $", {rejected} refused and kept here to correct" : "")
                                        + assigned,
            CentralOutcome.Held => "Held at the district office. NOT yet confirmed by the registry. Do not tell the family it is registered.",
            CentralOutcome.Unauthorized => "The tablet's sign-in has ended. A registrar must sign in again while there is signal.",
            CentralOutcome.Unreachable => "No connection to the registry. The births are kept and will go next time.",
            _ => $"Upload: {report.Upload}",
        };

        return upload + (report.BlockGranted is { } block ? $"\nNew numbers: {block.BlockStart} to {block.BlockEnd}" : "");
    }
}
