using NCBRS.Client.Localization;
using NCBRS.Models;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Coded answers on the registration form: shown in the registrar's language,
/// carrying their code, and never defaulted — an untouched picker is "not
/// answered", never the enum's first value.
/// </summary>
internal static class Choices
{
    public static Picker Choice<T>(string title, IEnumerable<T> values) where T : struct, Enum
        => new() { Title = title, ItemsSource = values.Select(value => new Option<T>(value)).ToList(), SelectedIndex = -1 };

    /// <summary>A choice that can be taken back: its first item says "none", so a slip of the finger is not permanent.</summary>
    public static Picker ChoiceOrNone<T>(string title, string none, IEnumerable<T> values) where T : struct, Enum
        => new()
        {
            Title = title,
            ItemsSource = new List<object> { new NoneOption(none) }.Concat(values.Select(value => (object)new Option<T>(value))).ToList(),
            SelectedIndex = -1,
        };

    public static T? Picked<T>(Picker picker) where T : struct, Enum
        => picker.SelectedItem is Option<T> option ? option.Value : null;

    public static void Select<T>(Picker picker, T? value) where T : struct, Enum
    {
        var items = picker.ItemsSource.Cast<object>().ToList();
        picker.SelectedIndex = value is null ? -1 : items.FindIndex(item => item is Option<T> option && option.Value.Equals(value.Value));
    }

    /// <summary>A coded answer, shown in the registrar's language, carrying its code.</summary>
    public sealed record Option<T>(T Value) where T : struct, Enum
    {
        public override string ToString() => Language.Name(Value);
    }

    private sealed record NoneOption(string Text)
    {
        public override string ToString() => Text;
    }
}

/// <summary>
/// An optional part of the registration, closed until the registrar opens it.
/// A phone-sized screen cannot show the whole form, and most of it is not asked
/// at most births; opened, it says so itself.
/// </summary>
internal sealed class Collapsible
{
    private readonly string _title;
    private bool _open;

    public Collapsible(string title, params View[] views)
    {
        _title = title;
        Header = Ui.SecondaryButton(string.Empty);
        Header.HorizontalOptions = LayoutOptions.Fill;
        Header.Clicked += (_, _) => Open(!_open);

        Content = new VerticalStackLayout { Spacing = Space.Md, IsVisible = false };
        Content.Add(Ui.Caption(Strings.Register_SectionOptional));
        foreach (var view in views)
        {
            Content.Add(view is Entry or Picker ? Ui.Labeled(view) : view);
        }

        Open(false);
    }

    public Button Header { get; }

    public VerticalStackLayout Content { get; }

    public void Open(bool open)
    {
        _open = open;
        Content.IsVisible = open;
        Header.Text = Language.Format(open ? Strings.Register_HideSection : Strings.Register_AddSection, _title);
    }
}

/// <summary>
/// A date that may not be known — a parent's birth, a marriage. A date picker
/// always holds a date, so "not known" is a box left unticked rather than
/// whatever date the picker happened to show.
/// </summary>
internal sealed class OptionalDate
{
    private readonly CheckBox _known = new();

    public OptionalDate(string knownLabel, DateTime suggested)
    {
        Picker = new DatePicker { MaximumDate = DateTime.Today, Date = suggested, Format = "d MMM yyyy", IsVisible = false };
        _known.CheckedChanged += (_, args) => Picker.IsVisible = args.Value;
        View = new VerticalStackLayout
        {
            Spacing = Space.Sm,
            Children =
            {
                new HorizontalStackLayout { Spacing = Space.Sm, Children = { _known, new Label { Text = knownLabel, FontSize = 16, FontFamily = Ui.Regular, VerticalOptions = LayoutOptions.Center } } },
                Ui.Input(Picker, trailingIcon: Icons.Calendar),
            },
        };
    }

    public DatePicker Picker { get; }

    public View View { get; }

    public DateOnly? Value => _known.IsChecked ? DateOnly.FromDateTime(Picker.Date ?? DateTime.Today) : null;

    public bool HasInput => _known.IsChecked;

    public void Set(DateOnly? value)
    {
        _known.IsChecked = value is not null;
        if (value is { } date)
        {
            Picker.Date = date.ToDateTime(TimeOnly.MinValue);
        }
    }
}

/// <summary>
/// One parent's details. Every part is optional, but details that are given
/// must name the parent — the core's rules say so in the registry's words.
/// Only the mother is asked a maiden surname.
/// </summary>
internal sealed class ParentFields
{
    private readonly Entry _given = Field(Strings.Register_GivenNames);
    private readonly Entry _surname = Field(Strings.Register_Surname);
    private readonly Entry? _maiden;
    private readonly OptionalDate _born = new(Strings.Register_DateOfBirthKnown, DateTime.Today.AddYears(-25));
    private readonly Entry _place = Field(Strings.Register_ParentPlaceOfBirth);
    private readonly Entry _occupation = Field(Strings.Register_Occupation);
    private readonly Entry _address = Field(Strings.Register_Address);
    private readonly Picker _document = Choices.ChoiceOrNone(Strings.Register_Document, Strings.Register_DocumentNone, Enum.GetValues<IdentityDocumentType>());
    private readonly Entry _documentNumber = Field(Strings.Register_DocumentNumber);

    public ParentFields(string title, bool isMother)
    {
        _maiden = isMother ? Field(Strings.Register_MaidenSurname) : null;

        var views = new List<View> { _given, _surname };
        if (_maiden is not null)
        {
            views.Add(_maiden);
        }

        views.AddRange([_born.View, _place, _occupation, _address, _document, _documentNumber]);
        Section = new Collapsible(title, [.. views.Select(view => view is Entry or Picker ? Ui.Labeled(view) : view)]);
    }

    public Collapsible Section { get; }

    private IEnumerable<Entry> Entries => new[] { _given, _surname, _maiden, _place, _occupation, _address, _documentNumber }.OfType<Entry>();

    public bool HasInput => Entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Text)) || _born.HasInput || _document.SelectedIndex > 0;

    /// <summary>The parent as the registry takes them, or null when nothing was given.</summary>
    public ParentDetails? Read()
    {
        if (!HasInput)
        {
            return null;
        }

        return new ParentDetails
        {
            GivenNames = Text(_given),
            Surname = Text(_surname),
            MaidenSurname = _maiden is null ? null : Text(_maiden),
            DateOfBirth = _born.Value,
            PlaceOfBirth = Text(_place),
            Occupation = Text(_occupation),
            Address = Text(_address),
            DocumentType = Choices.Picked<IdentityDocumentType>(_document),
            DocumentNumber = Text(_documentNumber),
        };
    }

    /// <summary>Filled from a birth as it was sent: the parts, or a one-piece name split at its last word.</summary>
    public void Fill(ParentDetails? details, string? fullName)
    {
        if (details is null && !string.IsNullOrWhiteSpace(fullName))
        {
            var (given, surname) = Split(fullName);
            details = new ParentDetails { GivenNames = given, Surname = surname };
        }

        _given.Text = details?.GivenNames;
        _surname.Text = details?.Surname;
        if (_maiden is not null)
        {
            _maiden.Text = details?.MaidenSurname;
        }

        _born.Set(details?.DateOfBirth);
        _place.Text = details?.PlaceOfBirth;
        _occupation.Text = details?.Occupation;
        _address.Text = details?.Address;
        Choices.Select(_document, details?.DocumentType);
        _documentNumber.Text = details?.DocumentNumber;
        Section.Open(HasInput);
    }

    public void Clear() => Fill(null, null);

    /// <summary>
    /// A one-piece name as given names and a surname: the last word is the
    /// surname, as a South Sudanese name ends in the father's. Used only to put
    /// a birth from before the fuller form back on the form; the registrar sees
    /// the split and can change it.
    /// </summary>
    public static (string? Given, string? Surname) Split(string? fullName)
    {
        var words = (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => (null, null),
            1 => (words[0], null),
            _ => (string.Join(' ', words[..^1]), words[^1]),
        };
    }

    private static Entry Field(string placeholder) => new() { Placeholder = placeholder };

    private static string? Text(Entry entry) => string.IsNullOrWhiteSpace(entry.Text) ? null : entry.Text.Trim();
}
