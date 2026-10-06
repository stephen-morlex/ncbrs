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

    public static T? Picked<T>(Picker picker) where T : struct, Enum
        => picker.SelectedItem is Option<T> option ? option.Value : null;

    public static void Select<T>(Picker picker, T? value) where T : struct, Enum
    {
        var items = picker.ItemsSource.Cast<object>().ToList();
        picker.SelectedIndex = value is null ? -1 : items.FindIndex(item => item is Option<T> option && option.Value.Equals(value.Value));
    }

    /// <summary>Chips for every value of an enum, each named in the registrar's language.</summary>
    public static ChoiceChips<T> Chips<T>(IEnumerable<T> values) where T : struct, Enum
        => new([.. values.Select(value => (value, Language.Name(value)))]);

    /// <summary>A coded answer, shown in the registrar's language, carrying its code.</summary>
    public sealed record Option<T>(T Value) where T : struct, Enum
    {
        public override string ToString() => Language.Name(Value);
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

        var label = Ui.Body(knownLabel);
        label.VerticalOptions = LayoutOptions.Center;
        Ui.Tappable(label, () => _known.IsChecked = !_known.IsChecked);

        View = new VerticalStackLayout
        {
            Spacing = Space.Sm,
            Children =
            {
                new HorizontalStackLayout { Spacing = Space.Sm, Children = { _known, label } },
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

/// <summary>Which identity document a parent showed, "none" included, so the answer can be taken back.</summary>
internal enum DocumentChoice
{
    None,
    NationalId,
    Passport,
    BirthCertificate,
    DrivingLicence,
}

/// <summary>
/// One parent's details, as one step of the form. Every part is optional, but
/// details that are given must name the parent — the core's rules say so in
/// the registry's words. Only the mother is asked a maiden surname.
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
    private readonly ChoiceChips<DocumentChoice> _document = new([
        (DocumentChoice.None, Strings.Register_DocumentNone),
        (DocumentChoice.NationalId, Language.Name(IdentityDocumentType.NationalId)),
        (DocumentChoice.Passport, Language.Name(IdentityDocumentType.Passport)),
        (DocumentChoice.BirthCertificate, Language.Name(IdentityDocumentType.BirthCertificate)),
        (DocumentChoice.DrivingLicence, Language.Name(IdentityDocumentType.DrivingLicence)),
    ]);
    private readonly Entry _documentNumber = Field(Strings.Register_DocumentNumber);

    public ParentFields(bool isMother)
    {
        _maiden = isMother ? Field(Strings.Register_MaidenSurname) : null;

        // The number belongs to a document, and only then.
        _documentNumber.IsVisible = false;
        _document.SelectionChanged += (_, _) => _documentNumber.IsVisible = Document is not null;

        var fields = new VerticalStackLayout { Spacing = Space.Xl };
        fields.Add(Ui.Labeled(_given));
        fields.Add(Ui.Labeled(_surname));
        if (_maiden is not null)
        {
            fields.Add(Ui.Labeled(_maiden));
        }

        fields.Add(Ui.Group(Strings.Register_DateOfBirth, _born.View));
        fields.Add(Ui.Labeled(_place));
        fields.Add(Ui.Labeled(_occupation));
        fields.Add(Ui.Labeled(_address));
        fields.Add(Ui.Labeled(Strings.Register_Document, _document));
        fields.Add(Ui.Labeled(_documentNumber));
        View = fields;
    }

    public View View { get; }

    private IdentityDocumentType? Document => _document.HasSelection
        ? _document.Selected switch
        {
            DocumentChoice.NationalId => IdentityDocumentType.NationalId,
            DocumentChoice.Passport => IdentityDocumentType.Passport,
            DocumentChoice.BirthCertificate => IdentityDocumentType.BirthCertificate,
            DocumentChoice.DrivingLicence => IdentityDocumentType.DrivingLicence,
            _ => null,
        }
        : null;

    private IEnumerable<Entry> Entries => new[] { _given, _surname, _maiden, _place, _occupation, _address, _documentNumber }.OfType<Entry>();

    public bool HasInput => Entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Text)) || _born.HasInput || Document is not null;

    /// <summary>The parent as the registry takes them, or null when nothing was given.</summary>
    public ParentDetails? Read()
    {
        if (!HasInput)
        {
            return null;
        }

        var document = Document;
        return new ParentDetails
        {
            GivenNames = Text(_given),
            Surname = Text(_surname),
            MaidenSurname = _maiden is null ? null : Text(_maiden),
            DateOfBirth = _born.Value,
            PlaceOfBirth = Text(_place),
            Occupation = Text(_occupation),
            Address = Text(_address),
            DocumentType = document,
            // A number typed before switching back to "none shown" is not sent.
            DocumentNumber = document is null ? null : Text(_documentNumber),
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
        if (details?.DocumentType is { } type)
        {
            _document.Selected = type switch
            {
                IdentityDocumentType.Passport => DocumentChoice.Passport,
                IdentityDocumentType.BirthCertificate => DocumentChoice.BirthCertificate,
                IdentityDocumentType.DrivingLicence => DocumentChoice.DrivingLicence,
                _ => DocumentChoice.NationalId,
            };
        }
        else
        {
            _document.Clear();
        }

        _documentNumber.Text = details?.DocumentNumber;
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
