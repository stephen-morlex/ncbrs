namespace NCBRS.Models;

/// <summary>
/// Any individual referenced by the system: the child, mother, father,
/// or a registrar. Kept generic so mother/father/child don't duplicate
/// identity fields across separate tables.
/// </summary>
public class Person
{
    public Guid PersonId { get; set; } = Guid.CreateVersion7();

    public required string FullName { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    /// <summary>
    /// Populated once this person is later issued a National ID.
    /// Nullable because most children won't have one at registration time.
    /// </summary>
    public string? NationalIdRef { get; set; }

    // The parts a registration records (2026-10-02). FullName stays the
    // composed whole (PersonNames.Compose): the signed certificate, matching
    // and search all read it. A correction to FullName clears these rather
    // than leave them contradicting it.
    public string? GivenNames { get; set; }

    public string? Surname { get; set; }

    /// <summary>The mother's surname before marriage. Nobody else's.</summary>
    public string? MaidenSurname { get; set; }

    public string? PlaceOfBirth { get; set; }

    /// <summary>Free text, as said. Not counted nationally; the coded groups in maternal statistics are.</summary>
    public string? Occupation { get; set; }

    public string? Address { get; set; }

    public IdentityDocumentType? IdentityDocumentType { get; set; }

    public string? IdentityDocumentNumber { get; set; }
}
