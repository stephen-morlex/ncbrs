using NCBRS.Client.App.Services;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The births the registry refused, each with its reasons. They are held on the
/// tablet — not sent again unchanged, which would only be refused again — until
/// a registrar corrects them. Nothing here deletes a birth: its number may
/// already be on a family's slip, and a birth the registry never received must
/// not quietly disappear from the one place that holds it.
/// </summary>
public sealed class RefusedPage : FlowPage
{
    public RefusedPage(DeviceHost host) : base("Refused births")
    {
        var refused = host.Session?.Facility.Refused ?? [];
        var list = new VerticalStackLayout { Spacing = 16 };

        foreach (var (record, reasons) in refused)
        {
            var correct = new Button { Text = $"Correct {record.Birth.Brn}" };
            correct.Clicked += (_, _) => Flow.Show(new RegisterPage(host, record, reasons));
            list.Add(new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new Label { Text = $"{record.Birth.Brn}: {record.Birth.ChildFullName}, born {record.Birth.DateOfBirth:d MMM yyyy}", FontAttributes = FontAttributes.Bold },
                    new Label
                    {
                        FontSize = 13, TextColor = Colors.DarkRed,
                        Text = string.Join("\n", reasons.Select(reason => $"• {reason.Message}")),
                    },
                    correct,
                },
            });
        }

        var back = new Button { Text = "Back to registering", BackgroundColor = Colors.Gray };
        back.Clicked += (_, _) => Flow.Advance(host);

        Build(
            Heading(refused.Count == 0 ? "No refused births" : "Births the registry refused"),
            Note(refused.Count == 0
                ? "Every birth sent has been accepted, or is waiting to sync."
                : "Each is kept on this tablet with the registry's reasons. Correct it and it is sent again at the next sync."),
            list,
            back);
    }
}
