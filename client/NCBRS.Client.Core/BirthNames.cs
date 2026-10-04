using NCBRS.Models;

namespace NCBRS.Client;

/// <summary>
/// The names on a birth as the registry will store them, whichever form the
/// request is in. The fuller form names people in parts and leaves the
/// one-piece fields empty, so anything that shows a name — the slip, the
/// confirmation, the list of refused births — reads it here rather than from
/// <see cref="RegisterBirthRequest.ChildFullName"/>, which a new birth no
/// longer fills.
/// </summary>
public static class BirthNames
{
    public static string Child(RegisterBirthRequest birth) => birth.EffectiveChildFullName;

    public static string? Mother(RegisterBirthRequest birth) => Parent(birth.Mother, birth.MotherFullName);

    public static string? Father(RegisterBirthRequest birth) => Parent(birth.Father, birth.FatherFullName);

    private static string? Parent(ParentDetails? details, string? fullName)
    {
        var name = details?.HasName == true ? PersonNames.Compose(details.GivenNames, details.Surname) : fullName;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
