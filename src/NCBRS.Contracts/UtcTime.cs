namespace NCBRS;

/// <summary>
/// Every timestamp and date in the registry is written as UTC. This reads one
/// back as UTC whatever kind it arrives with.
///
/// SQLite hands a value back as <see cref="DateTimeKind.Unspecified"/>, and
/// <see cref="DateTime.ToUniversalTime"/> treats that as the server's local time
/// and shifts it. A date of birth is midnight UTC, so on a server in South
/// Sudan (UTC+2) the shift moves it to the previous day: a certificate signed
/// the child's birthday a day early, and an age at death came out a day long,
/// which decides whether a death is neonatal. It had already happened, and
/// been fixed locally, for revocation lists, amendment drift checks and
/// dashboard ranges; this is the one place it lives now.
///
/// An unspecified kind is labelled UTC, not converted. A local one is
/// converted, because something in this process made it local on purpose.
/// </summary>
public static class UtcTime
{
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
