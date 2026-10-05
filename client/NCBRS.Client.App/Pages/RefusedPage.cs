using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;

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
    public RefusedPage(DeviceHost host) : base(Strings.Refused_Title)
    {
        var refused = host.Session?.Facility.Refused ?? [];
        var list = new VerticalStackLayout { Spacing = Space.Md };

        foreach (var (record, reasons) in refused)
        {
            var correct = Ui.PrimaryButton(Language.Format(Strings.Refused_Correct, record.Birth.Brn));
            correct.Clicked += (_, _) => Flow.Show(new RegisterPage(host, record, reasons));
            list.Add(Ui.Card(
                Ui.Heading(Language.Format(Strings.Refused_Item, record.Birth.Brn, BirthNames.Child(record.Birth), record.Birth.DateOfBirth)),
                Ui.Badge(Strings.Badge_Refused, Tone.Danger),
                new Label
                {
                    FontSize = 15, FontFamily = Ui.Regular, TextColor = Ui.Danger,
                    Text = string.Join("\n", reasons.Select(reason => $"• {reason.Message}")),
                },
                correct));
        }

        var back = Ui.GhostButton(Strings.Refused_Back);
        // Unlocked, the bottom bar is the way back; before, this is the only one.
        back.IsVisible = host.UnlockedAs is null;
        back.Clicked += (_, _) => Flow.Advance(host);

        BuildFor(host, Pages.Section.Records,
            Heading(refused.Count == 0 ? Strings.Refused_HeadingNone : Strings.Refused_Heading),
            Note(refused.Count == 0
                ? Strings.Refused_NoneNote
                : Strings.Refused_Note),
            list,
            back);
    }
}
