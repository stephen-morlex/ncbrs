using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Client.Storage;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The births on this tablet, newest first, grouped by day: registrations
/// still being typed (drafts), births waiting to sync, those the registry
/// refused, and the month of confirmed ones the tablet keeps. Searched by
/// name or number and filtered by where they stand. A draft continues where
/// it stopped, a refused birth opens to be corrected, a registered one to
/// print its certificate; nothing here deletes a birth.
/// </summary>
public sealed class RecordsPage : FlowPage
{
    private enum Filter
    {
        All,
        Drafts,
        Waiting,
        Registered,
        Refused,
    }

    private readonly DeviceHost _host;
    private readonly IReadOnlyList<TabletRecord> _records;
    private readonly IReadOnlyList<FormDraft> _drafts;
    private readonly Entry _search = new() { Placeholder = Strings.Records_Search, ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private readonly ChoiceChips<Filter> _filter;
    private readonly VerticalStackLayout _list = new() { Spacing = Space.Xl };

    public RecordsPage(DeviceHost host) : base(Strings.Records_Title)
    {
        _host = host;
        _records = TabletRecords.All(host);
        _drafts = [.. host.State.Drafts];

        _filter = new ChoiceChips<Filter>([
            (Filter.All, Strings.Records_All),
            (Filter.Drafts, Strings.Records_Drafts),
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
        Filter.Drafts => false,
        Filter.Waiting => record.State == RecordState.Waiting,
        Filter.Registered => record.State == RecordState.Registered,
        Filter.Refused => record.State == RecordState.Refused,
        _ => true,
    };

    private bool ShowsDrafts => _filter.Selected is Filter.All or Filter.Drafts;

    private void Show()
    {
        _list.Clear();
        if (_records.Count == 0 && _drafts.Count == 0)
        {
            _list.Add(Ui.Card(Ui.Body(Strings.Records_Empty, Ui.TextMuted)));
            return;
        }

        // Drafts and births in one list, each with the moment it belongs to:
        // a draft when it was last saved, a birth when it was registered.
        var rows = new List<(DateTime When, View Row)>();
        if (ShowsDrafts)
        {
            rows.AddRange(_drafts
                .Where(draft => DraftMatches(draft, _search.Text))
                .Select(draft => (draft.UpdatedAtUtc, DraftRow(draft))));
        }

        rows.AddRange(_records
            .Where(Shows)
            .Where(record => TabletRecords.Matches(record, _search.Text))
            .Select(record => (record.RegisteredAtUtc, TabletRecords.Row(_host, record, Go))));

        if (rows.Count == 0)
        {
            _list.Add(Ui.Body(Strings.Records_None, Ui.TextMuted));
            return;
        }

        foreach (var day in rows.OrderByDescending(row => row.When).GroupBy(row => row.When.ToLocalTime().Date))
        {
            _list.Add(Ui.Group(TabletRecords.DayName(day.Key), Ui.ListCard([.. day.Select(row => row.Row)])));
        }
    }

    /// <summary>What a draft is called: the child, or failing that the mother's baby, or nothing yet.</summary>
    private static string DraftName(FormDraft draft)
    {
        var child = BirthNames.Child(draft.Birth);
        if (!string.IsNullOrWhiteSpace(child))
        {
            return child;
        }

        return BirthNames.Mother(draft.Birth) is { Length: > 0 } mother
            ? Language.Format(Strings.Records_BabyOf, mother)
            : Strings.Records_UnnamedDraft;
    }

    private static bool DraftMatches(FormDraft draft, string? search)
        => string.IsNullOrWhiteSpace(search) || DraftName(draft).Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>A registration still being typed, and the way back into it at the step it reached.</summary>
    private View DraftRow(FormDraft draft)
    {
        var name = DraftName(draft);
        var go = new Label
        {
            Text = Strings.Records_Continue,
            FontSize = 15,
            FontFamily = Ui.SemiBold,
            TextColor = Ui.Primary,
        };

        var avatar = Ui.Avatar(name);
        avatar.BackgroundColor = Ui.Muted;
        return Ui.Row(avatar, name, null, go,
            () => _ = Go(new RegisterPage(_host, draft: draft)),
            below: Ui.Badge(Language.Format(Strings.Badge_Draft, draft.Step, 5), Tone.Neutral));
    }
}
