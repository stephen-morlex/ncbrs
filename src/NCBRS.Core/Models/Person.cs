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
}
