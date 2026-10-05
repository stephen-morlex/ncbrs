using System.Globalization;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Models;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The births on this tablet, newest first, grouped by the day they were
/// registered: each with its number and a badge saying where it stands. A
/// refused birth opens to be corrected; nothing here deletes one.
///
/// For now these are the births the registry has not yet confirmed, because
/// the tablet keeps a birth only until then. Drafts and a month of confirmed
/// births follow with the history the tablet will keep.
/// </summary>
public sealed class RecordsPage : FlowPage
{
    public RecordsPage(DeviceHost host) : base(Strings.Records_Title)
    {
        var refused = (host.Session?.Facility.Refused ?? [])
            .ToDictionary(entry => entry.Record.Birth.Brn, entry => entry.Reasons);

        var views = new List<View> { Ui.PageHeader(Strings.Records_Title, Strings.Records_Subtitle) };

        var days = host.State.Outbox
            .OrderByDescending(record => record.Birth.RegisteredAtUtc ?? DateTime.MinValue)
            .GroupBy(record => (record.Birth.RegisteredAtUtc ?? DateTime.UtcNow).ToLocalTime().Date)
            .ToList();

        if (days.Count == 0)
        {
            views.Add(Ui.Card(Ui.Body(Strings.Records_Empty, Ui.TextMuted)));
        }

        foreach (var day in days)
        {
            var rows = day.Select(record => Row(host, record, refused)).ToArray();
            views.Add(Ui.Group(DayName(day.Key), Ui.ListCard(rows)));
        }

        BuildInside(host, Section.Records, [.. views]);
    }

    private View Row(DeviceHost host, SyncBirthRecord record, IReadOnlyDictionary<string, IReadOnlyList<ApiError>> refused)
    {
        var birth = record.Birth;
        var name = BirthNames.Child(birth);
        var line = Language.Format(Strings.Records_Born, birth.DateOfBirth, Language.Name(birth.Sex));

        var badges = new HorizontalStackLayout { Spacing = Space.Sm };
        Action? open = null;
        if (refused.TryGetValue(birth.Brn, out var reasons))
        {
            badges.Add(Ui.Badge(Strings.Badge_Refused, Tone.Danger));
            open = () => _ = Go(new RegisterPage(host, record, reasons));
        }
        else
        {
            badges.Add(Ui.Badge(Strings.Badge_Waiting, Tone.Pending));
        }

        var below = new VerticalStackLayout
        {
            Spacing = Space.Xs,
            Children = { Ui.Number(birth.Brn, 13, Ui.TextMuted), badges },
        };
        return Ui.Row(Ui.Avatar(name), name, line, onTap: open, below: below);
    }

    private static string DayName(DateTime day)
    {
        var today = DateTime.Now.Date;
        return day == today ? Strings.Records_Today
            : day == today.AddDays(-1) ? Strings.Records_Yesterday
            : day.ToString("d MMMM yyyy", Language.Current ?? CultureInfo.CurrentCulture);
    }
}
