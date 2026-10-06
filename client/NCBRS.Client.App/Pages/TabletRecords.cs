using System.Globalization;
using NCBRS.Client.App.Services;
using NCBRS.Client.Localization;
using NCBRS.Models;

namespace NCBRS.Client.App.Pages;

/// <summary>Where a birth on the tablet stands.</summary>
public enum RecordState
{
    /// <summary>On the tablet, not yet sent or not yet answered.</summary>
    Waiting,

    /// <summary>The registry refused it; held here until corrected.</summary>
    Refused,

    /// <summary>The registry confirmed it; kept for 30 days.</summary>
    Registered,
}

/// <summary>A birth as the Records list and the home screen show it.</summary>
public sealed record TabletRecord(
    string Brn,
    string Name,
    DateTime DateOfBirth,
    Sex Sex,
    DateTime RegisteredAtUtc,
    RecordState State,
    bool Printed,
    SyncBirthRecord? Held,
    IReadOnlyList<ApiError> Reasons);

/// <summary>
/// Every birth the tablet knows about, in one list: those still in the outbox
/// (waiting, or refused and held) and the month of confirmed ones it keeps.
/// The home screen's "Recent births" and the Records screen read the same list
/// and draw the same rows, so they cannot disagree about a birth.
/// </summary>
public static class TabletRecords
{
    public static IReadOnlyList<TabletRecord> All(DeviceHost host)
    {
        if (host.Session?.Facility is not { } facility)
        {
            return [];
        }

        var refused = facility.Refused.ToDictionary(entry => entry.Record.Birth.Brn, entry => entry.Reasons);
        var records = new List<TabletRecord>();

        foreach (var held in host.State.Outbox)
        {
            var birth = held.Birth;
            var isRefused = refused.TryGetValue(birth.Brn, out var reasons);
            records.Add(new TabletRecord(
                birth.Brn, BirthNames.Child(birth), birth.DateOfBirth, birth.Sex,
                birth.RegisteredAtUtc ?? DateTime.UtcNow,
                isRefused ? RecordState.Refused : RecordState.Waiting,
                Printed: false, held, reasons ?? []));
        }

        foreach (var kept in facility.Recent.All)
        {
            records.Add(new TabletRecord(
                kept.Brn, kept.ChildName, kept.DateOfBirth, kept.Sex, kept.RegisteredAtUtc,
                RecordState.Registered, kept.CertificatePrinted, null, []));
        }

        return [.. records.OrderByDescending(record => record.RegisteredAtUtc)];
    }

    /// <summary>
    /// Whether a record is the one being looked for: the name in any case, or
    /// the number as it might be typed at a counter (any case, spaces or dashes
    /// left out).
    /// </summary>
    public static bool Matches(TabletRecord record, string? search)
    {
        var wanted = (search ?? "").Trim();
        if (wanted.Length == 0)
        {
            return true;
        }

        if (record.Name.Contains(wanted, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        static string Bare(string text) => new([.. Language.WesternDigits(text).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);
        var bare = Bare(wanted);
        return bare.Length > 0 && Bare(record.Brn).Contains(bare, StringComparison.Ordinal);
    }

    /// <summary>
    /// One birth as a list row: initials, name, date of birth and sex, the
    /// number, and badges for where it stands. A refused birth opens to be
    /// corrected; a registered one opens to print its certificate.
    /// </summary>
    public static View Row(DeviceHost host, TabletRecord record, Func<Page, Task> go)
    {
        var badges = new HorizontalStackLayout { Spacing = Space.Sm };
        badges.Add(record.State switch
        {
            RecordState.Registered => Ui.Badge(Strings.Badge_Registered, Tone.Primary),
            RecordState.Refused => Ui.Badge(Strings.Badge_Refused, Tone.Danger),
            _ => Ui.Badge(Strings.Badge_Waiting, Tone.Pending),
        });
        if (record.Printed)
        {
            badges.Add(Ui.Badge(Strings.Badge_Printed, Tone.Neutral));
        }

        Action? open = record.State switch
        {
            RecordState.Refused when record.Held is { } held => () => _ = go(new RegisterPage(host, held, record.Reasons)),
            RecordState.Registered => () => _ = go(new PrintCertificatePage(host, record.Brn)),
            _ => null,
        };

        var below = new VerticalStackLayout
        {
            Spacing = Space.Xs,
            Children = { Ui.Number(record.Brn, 13, Ui.TextMuted), badges },
        };
        var line = Language.Format(Strings.Records_Born, record.DateOfBirth, Language.Name(record.Sex));
        return Ui.Row(Ui.Avatar(record.Name), record.Name, line, onTap: open, below: below);
    }

    /// <summary>"Today", "Yesterday", or the date, for a day's group of births.</summary>
    public static string DayName(DateTime localDay)
    {
        var today = DateTime.Now.Date;
        return localDay == today ? Strings.Records_Today
            : localDay == today.AddDays(-1) ? Strings.Records_Yesterday
            : localDay.ToString("d MMMM yyyy", Language.Current ?? CultureInfo.CurrentCulture);
    }
}
