using System.Globalization;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Corrections to the fuller registration's fields: the child's given names
/// and surname, the place of birth, each parent's details, the marriage and
/// the proof of address.
///
/// **All of them wait for a second registrar.** The settled rule is that only
/// the clinical measurements apply at once; everything else describes who the
/// record is about or a legal fact of it. The child's name parts are also
/// certificate fields, because they recompose the signed full name.
///
/// Stored and compared in the same string form as every other correction --
/// enums by name, dates as <c>yyyy-MM-dd</c>, text cleaned the way
/// registration cleans it -- so the drift check at approval compares like with
/// like. A text value given as "" clears an optional field.
/// </summary>
public partial class AmendmentService
{
    private static readonly string[] Parents = [AmendmentFields.Mother, AmendmentFields.Father];

    private static void CollectFuller(BirthRecord record, AmendBirthRecordRequest request, List<FieldChange> changes)
    {
        CompareText(changes, record, AmendmentFields.ChildGivenNames, request.ChildGivenNames);
        CompareText(changes, record, AmendmentFields.ChildSurname, request.ChildSurname);

        if (request.PlaceOfBirthKind is { } kind && kind != record.PlaceOfBirthKind)
        {
            changes.Add(new FieldChange(AmendmentFields.PlaceOfBirthKind, record.PlaceOfBirthKind?.ToString(), kind.ToString()));
        }

        CompareText(changes, record, AmendmentFields.PlaceOfBirth, request.PlaceOfBirth);

        foreach (var parent in Parents)
        {
            if ((parent == AmendmentFields.Mother ? request.Mother : request.Father) is not { } details)
            {
                continue;
            }

            CompareText(changes, record, AmendmentFields.Of(parent, AmendmentFields.GivenNames), details.GivenNames);
            CompareText(changes, record, AmendmentFields.Of(parent, AmendmentFields.Surname), details.Surname);
            if (parent == AmendmentFields.Mother)
            {
                CompareText(changes, record, AmendmentFields.Of(parent, AmendmentFields.MaidenSurname), details.MaidenSurname);
            }

            CompareValue(changes, record, AmendmentFields.Of(parent, AmendmentFields.DateOfBirth), Day(details.DateOfBirth));
            CompareText(changes, record, AmendmentFields.Of(parent, AmendmentFields.ParentPlaceOfBirth), details.PlaceOfBirth);
            CompareText(changes, record, AmendmentFields.Of(parent, AmendmentFields.Occupation), details.Occupation);
            CompareText(changes, record, AmendmentFields.Of(parent, AmendmentFields.Address), details.Address);
            CompareValue(changes, record, AmendmentFields.Of(parent, AmendmentFields.DocumentType), details.DocumentType?.ToString());
            CompareText(changes, record, AmendmentFields.Of(parent, AmendmentFields.DocumentNumber), details.DocumentNumber);
        }

        if (request.Marriage is { } marriage)
        {
            CompareValue(changes, record, AmendmentFields.MarriageDate, Day(marriage.Date));
            CompareText(changes, record, AmendmentFields.MarriageCertificateNumber, marriage.CertificateNumber);
        }

        if (request.ProofOfAddress is { } proof)
        {
            CompareText(changes, record, AmendmentFields.ProofOfAddressKind, proof.Kind);
            CompareText(changes, record, AmendmentFields.ProofOfAddressReference, proof.Reference);
        }
    }

    /// <summary>
    /// A correction the record cannot take as given, or null. Answered before
    /// anything is stored, because each of these would leave the record
    /// describing someone it did not mean to.
    /// </summary>
    private static string? Incomplete(BirthRecord record, List<FieldChange> changes)
    {
        bool Changes(string field) => changes.Any(change => change.Field == field);

        // A name registered whole has no parts. Correcting one part would
        // compose a full name from that part alone and drop the rest.
        var child = record.ChildPerson!;
        if (child.GivenNames is null && child.Surname is null
            && Changes(AmendmentFields.ChildGivenNames) != Changes(AmendmentFields.ChildSurname))
        {
            return "This child's name was registered whole, not in parts. Give both the given names and the surname.";
        }

        foreach (var parent in Parents)
        {
            var person = parent == AmendmentFields.Mother ? record.MotherPerson : record.FatherPerson;
            var who = parent.ToLowerInvariant();
            var given = Changes(AmendmentFields.Of(parent, AmendmentFields.GivenNames));
            var surname = Changes(AmendmentFields.Of(parent, AmendmentFields.Surname));
            var any = changes.Any(change => change.Field.StartsWith(parent + ".", StringComparison.Ordinal));

            if (person is null && any && !given && !surname)
            {
                return $"No {who} is recorded on this birth. Give the {who}'s name to add the {who}'s details.";
            }

            if (person is not null && person.GivenNames is null && person.Surname is null && given != surname)
            {
                return $"The {who}'s name was registered whole, not in parts. Give both the {who}'s given names and surname.";
            }

            // Details must name the parent, at registration and after.
            if ((given || surname)
                && After(changes, AmendmentFields.Of(parent, AmendmentFields.GivenNames), person?.GivenNames) is null
                && After(changes, AmendmentFields.Of(parent, AmendmentFields.Surname), person?.Surname) is null)
            {
                return $"The {who}'s name cannot be removed: give the {who}'s given names or surname.";
            }
        }

        // The place of birth is required, and a birth away from the facility
        // says where. Judged on the record as it would read afterwards.
        var kind = After(changes, AmendmentFields.PlaceOfBirthKind, record.PlaceOfBirthKind?.ToString());
        var where = After(changes, AmendmentFields.PlaceOfBirth, record.PlaceOfBirth);
        var placeTouched = Changes(AmendmentFields.PlaceOfBirthKind) || Changes(AmendmentFields.PlaceOfBirth);
        if (placeTouched && kind is not null && kind != nameof(PlaceOfBirthKind.ThisFacility) && where is null)
        {
            return "Say where the birth happened: only a birth at this facility needs no description.";
        }

        return null;
    }

    /// <summary>What a field would read once these changes are applied.</summary>
    private static string? After(List<FieldChange> changes, string field, string? current)
        => changes.FirstOrDefault(change => change.Field == field) is { } change ? change.NewValue : current;

    /// <summary>The record's value for a fuller-registration field, in its stored string form.</summary>
    private static string? ReadFuller(BirthRecord record, string field)
    {
        switch (field)
        {
            case AmendmentFields.ChildGivenNames: return record.ChildPerson?.GivenNames;
            case AmendmentFields.ChildSurname: return record.ChildPerson?.Surname;
            case AmendmentFields.PlaceOfBirthKind: return record.PlaceOfBirthKind?.ToString();
            case AmendmentFields.PlaceOfBirth: return record.PlaceOfBirth;
            case AmendmentFields.MarriageDate: return Day(record.ParentsMarriageDate);
            case AmendmentFields.MarriageCertificateNumber: return record.MarriageCertificateNumber;
            case AmendmentFields.ProofOfAddressKind: return record.ProofOfAddressKind;
            case AmendmentFields.ProofOfAddressReference: return record.ProofOfAddressReference;
        }

        var (parent, part) = Split(field);
        var person = parent switch
        {
            AmendmentFields.Mother => record.MotherPerson,
            AmendmentFields.Father => record.FatherPerson,
            _ => null,
        };

        return person is null ? null : part switch
        {
            AmendmentFields.GivenNames => person.GivenNames,
            AmendmentFields.Surname => person.Surname,
            AmendmentFields.MaidenSurname => person.MaidenSurname,
            AmendmentFields.DateOfBirth => Day(person.DateOfBirth),
            AmendmentFields.ParentPlaceOfBirth => person.PlaceOfBirth,
            AmendmentFields.Occupation => person.Occupation,
            AmendmentFields.Address => person.Address,
            AmendmentFields.DocumentType => person.IdentityDocumentType?.ToString(),
            AmendmentFields.DocumentNumber => person.IdentityDocumentNumber,
            _ => null,
        };
    }

    /// <summary>Writes one fuller-registration change; the names it touches are recomposed after.</summary>
    private void ApplyFuller(BirthRecord record, FieldChange change)
    {
        var value = change.NewValue;

        switch (change.Field)
        {
            case AmendmentFields.ChildGivenNames: record.ChildPerson!.GivenNames = value; return;
            case AmendmentFields.ChildSurname: record.ChildPerson!.Surname = value; return;
            case AmendmentFields.PlaceOfBirthKind: record.PlaceOfBirthKind = Enum.Parse<PlaceOfBirthKind>(value!); return;
            case AmendmentFields.PlaceOfBirth: record.PlaceOfBirth = value; return;
            case AmendmentFields.MarriageDate: record.ParentsMarriageDate = ParseDay(value); return;
            case AmendmentFields.MarriageCertificateNumber: record.MarriageCertificateNumber = value; return;
            case AmendmentFields.ProofOfAddressKind: record.ProofOfAddressKind = value; return;
            case AmendmentFields.ProofOfAddressReference: record.ProofOfAddressReference = value; return;
        }

        var (parent, part) = Split(change.Field);
        var person = parent == AmendmentFields.Mother
            ? record.MotherPerson ??= NewParent()
            : record.FatherPerson ??= NewParent();

        switch (part)
        {
            case AmendmentFields.GivenNames: person.GivenNames = value; break;
            case AmendmentFields.Surname: person.Surname = value; break;
            case AmendmentFields.MaidenSurname: person.MaidenSurname = value; break;
            case AmendmentFields.DateOfBirth: person.DateOfBirth = ParseDay(value); break;
            case AmendmentFields.ParentPlaceOfBirth: person.PlaceOfBirth = value; break;
            case AmendmentFields.Occupation: person.Occupation = value; break;
            case AmendmentFields.Address: person.Address = value; break;
            case AmendmentFields.DocumentType:
                person.IdentityDocumentType = value is null ? null : Enum.Parse<IdentityDocumentType>(value);
                break;
            case AmendmentFields.DocumentNumber: person.IdentityDocumentNumber = value; break;
        }
    }

    /// <summary>
    /// The full name is what the signed certificate, matching and search read,
    /// so it is recomposed from the parts whenever a part was corrected -- the
    /// same composition registration uses.
    /// </summary>
    private static void RecomposeNames(BirthRecord record, IReadOnlyList<FieldChange> changes)
    {
        bool Touched(string given, string surname)
            => changes.Any(change => change.Field == given || change.Field == surname);

        if (Touched(AmendmentFields.ChildGivenNames, AmendmentFields.ChildSurname))
        {
            Recompose(record.ChildPerson!);
        }

        foreach (var parent in Parents)
        {
            var person = parent == AmendmentFields.Mother ? record.MotherPerson : record.FatherPerson;
            if (person is not null && Touched(AmendmentFields.Of(parent, AmendmentFields.GivenNames),
                    AmendmentFields.Of(parent, AmendmentFields.Surname)))
            {
                Recompose(person);
            }
        }
    }

    private static void Recompose(Person person) => person.FullName = PersonNames.Compose(person.GivenNames, person.Surname);

    /// <summary>
    /// A parent named for the first time by a correction. Its full name is
    /// composed once its parts are applied; <see cref="Incomplete"/> has
    /// already refused details that would leave it nameless.
    /// </summary>
    private Person NewParent()
    {
        var person = new Person { FullName = string.Empty };
        db.People.Add(person);
        return person;
    }

    /// <summary>Text: null leaves it alone, "" clears it (stored as null, as an omitted part is at registration).</summary>
    private static void CompareText(List<FieldChange> changes, BirthRecord record, string field, string? proposed)
    {
        if (proposed is not null)
        {
            Record(changes, record, field, CleanText(proposed));
        }
    }

    /// <summary>A date or a coded answer: null leaves it alone. Neither can be cleared, only corrected.</summary>
    private static void CompareValue(List<FieldChange> changes, BirthRecord record, string field, string? proposed)
    {
        if (proposed is not null)
        {
            Record(changes, record, field, proposed);
        }
    }

    private static void Record(List<FieldChange> changes, BirthRecord record, string field, string? proposed)
    {
        var current = ReadFuller(record, field);
        if (!string.Equals(current, proposed, StringComparison.Ordinal))
        {
            changes.Add(new FieldChange(field, current, proposed));
        }
    }

    /// <summary>Trimmed, inner whitespace collapsed, empty as null -- as registration stores text.</summary>
    private static string? CleanText(string value)
    {
        var cleaned = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? Day(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDay(string? value)
        => value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static (string Parent, string Part) Split(string field)
    {
        var dot = field.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (string.Empty, field) : (field[..dot], field[(dot + 1)..]);
    }
}
