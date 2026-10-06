using NCBRS.Models;

namespace NCBRS.Client.Sync;

/// <summary>
/// A birth this tablet registered and the registry has confirmed, kept for a
/// while so a registrar can find this week's births and reprint one. Only what
/// the Records list shows: the number, the child's name, the date of birth
/// and sex, when it was registered and confirmed, and whether its certificate
/// was printed here.
/// </summary>
/// <param name="ProvisionalBrn">The <c>PROV-</c> number the tablet issued, when the registry assigned a real one in its place.</param>
public sealed record RecentBirth(
    string Brn,
    string ChildName,
    DateTime DateOfBirth,
    Sex Sex,
    DateTime RegisteredAtUtc,
    DateTime ConfirmedAtUtc,
    bool CertificatePrinted = false,
    string? ProvisionalBrn = null);

/// <summary>
/// The births this tablet registered in the last <see cref="KeptFor"/>, after
/// the registry confirmed them (decided with the user, 2026-10-05).
///
/// The outbox lets a birth go the moment the registry has it, which is right
/// for the outbox: a settled birth is the registry's to keep. But a registrar
/// asked to reprint a certificate, or to find the birth they registered on
/// Monday, needs the list of what they did. So the tablet keeps a short one,
/// in the same encrypted store, and keeps it short on purpose: <b>a lost tablet
/// should hold at most a month of names</b>, not every birth it ever
/// registered. Every birth is forgotten <see cref="KeptFor"/> after it was
/// registered, whatever else happened to it.
///
/// A pure state machine, like the outbox: the session persists
/// <see cref="All"/>.
/// </summary>
public sealed class RecentBirths
{
    /// <summary>How long a registered birth stays on the tablet.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    private readonly List<RecentBirth> _births;

    public RecentBirths(IEnumerable<RecentBirth>? births = null) => _births = [.. births ?? []];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentBirth> All => [.. _births.OrderByDescending(birth => birth.RegisteredAtUtc)];

    /// <summary>
    /// The births a sync settled, as the registry now holds them. A birth the
    /// registry gave a real number in place of a provisional one is kept under
    /// the real number, with the provisional one beside it, because the family
    /// may still hold the slip. Settled again (a re-sent batch), a birth is not
    /// listed twice.
    /// </summary>
    public void Record(OutboxSettlement settlement, DateTime nowUtc)
    {
        foreach (var outcome in settlement.Settled)
        {
            if (settlement.SettledBirths?.FirstOrDefault(record => record.Birth.Brn == outcome.Brn) is not { } record)
            {
                continue;
            }

            var birth = record.Birth;
            var brn = outcome.AssignedBrn ?? outcome.Brn;
            _births.RemoveAll(kept => kept.Brn == brn);
            _births.Add(new RecentBirth(
                brn,
                BirthNames.Child(birth),
                birth.DateOfBirth,
                birth.Sex,
                birth.RegisteredAtUtc ?? nowUtc,
                nowUtc,
                ProvisionalBrn: outcome.AssignedBrn is null ? null : outcome.Brn));
        }

        Forget(nowUtc);
    }

    /// <summary>Note that a birth's certificate was printed on this tablet.</summary>
    public void MarkPrinted(string brn)
    {
        var index = _births.FindIndex(birth => string.Equals(birth.Brn, brn.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _births[index] = _births[index] with { CertificatePrinted = true };
        }
    }

    /// <summary>Drop every birth registered more than <see cref="KeptFor"/> ago.</summary>
    public void Forget(DateTime nowUtc) => _births.RemoveAll(birth => birth.RegisteredAtUtc < nowUtc - KeptFor);
}
