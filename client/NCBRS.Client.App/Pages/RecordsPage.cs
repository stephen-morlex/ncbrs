using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The births on this tablet, newest first, grouped by the day they were
/// registered: those waiting to sync, those the registry refused, and the
/// month of confirmed ones the tablet keeps. Searched by name or number and
/// filtered by where they stand. A refused birth opens to be corrected, a
/// registered one to print its certificate; nothing here deletes a birth.
/// </summary>
public sealed class RecordsPage : FlowPage
{
    private enum Filter
    {
        All,
        Waiting,
        Registered,
        Refused,
    }

    private readonly DeviceHost _host;
    private readonly IReadOnlyList<TabletRecord> _records;
    private readonly Entry _search = new() { Placeholder = Strings.Records_Search, ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private readonly ChoiceChips<Filter> _filter;
    private readonly VerticalStackLayout _list = new() { Spacing = Space.Xl };

    public RecordsPage(DeviceHost host) : base(Strings.Records_Title)
    {
        _host = host;
        _records = TabletRecords.All(host);

        _filter = new ChoiceChips<Filter>([
            (Filter.All, Strings.Records_All),
            (Filter.Waiting, Strings.Badge_Waiting),
            (Filter.Registered, Strings.Badge_Registered),
            (Filter.Refused, Strings.Badge_Refused),
        ]);
        _filter.Selected = Filter.All;
        _filter.SelectionChanged += (_, _) => Show();
        _search.TextChanged += (_, _) => Show();

        Show();
        BuildInside(host, Section.Records,
            Ui.PageHeader(Strings.Records_Title, Strings.Records_Subtitle),
            new VerticalStackLayout { Spacing = Space.Md, Children = { Ui.Input(_search, trailingIcon: Icons.Search), _filter } },
            _list);
    }

    private bool Shows(TabletRecord record) => _filter.Selected switch
    {
        Filter.Waiting => record.State == RecordState.Waiting,
        Filter.Registered => record.State == RecordState.Registered,
        Filter.Refused => record.State == RecordState.Refused,
        _ => true,
    };

    private void Show()
    {
        _list.Clear();
        if (_records.Count == 0)
        {
            _list.Add(Ui.Card(Ui.Body(Strings.Records_Empty, Ui.TextMuted)));
            return;
        }

        var shown = _records.Where(Shows).Where(record => TabletRecords.Matches(record, _search.Text)).ToList();
        if (shown.Count == 0)
        {
            _list.Add(Ui.Body(Strings.Records_None, Ui.TextMuted));
            return;
        }

        foreach (var day in shown.GroupBy(record => record.RegisteredAtUtc.ToLocalTime().Date))
        {
            _list.Add(Ui.Group(TabletRecords.DayName(day.Key),
                Ui.ListCard([.. day.Select(record => TabletRecords.Row(_host, record, Go))])));
        }
    }
}
