using NCBRS.Client.Auth;
using NCBRS.Client.Time;
using NCBRS.Models;

namespace NCBRS.Client.Storage;

/// <summary>
/// Unlocking a shared tablet: the registrar picks their name and enters their
/// PIN, checked offline against the credential the centre issued. What makes it
/// a device lock rather than a list of personal ones is the counter — one
/// <see cref="DeviceState.Attempts"/> for the tablet, so moving to the next name
/// does not buy a fresh set of guesses.
///
/// The caller saves the state after <em>every</em> attempt, right or wrong, so
/// force-quitting between guesses resets nothing.
/// </summary>
public static class StaffUnlock
{
    /// <param name="now">The monotonic clock, never the date: the cooldown must not be skippable from Settings.</param>
    public static UnlockResult Attempt(DeviceState state, Guid registrarId, string? pin, MonotonicReading now)
    {
        var person = state.Staff.Find(staff => staff.RegistrarId == registrarId)
                     ?? throw new ArgumentException("Nobody by that id may unlock this tablet.", nameof(registrarId));

        var attempts = state.Attempts;

        // A lock saved by an older version, on the wall clock: its progress
        // cannot be trusted, so it restarts in full on the monotonic clock.
        var lockedAt = attempts.LockedAt ?? (attempts.LockedUntilUtc is not null ? now : null);

        var @lock = new OfflinePinLock(
            person.Credential,
            failedAttempts: attempts.FailedAttempts,
            lockouts: attempts.Lockouts,
            lockedAt: lockedAt,
            lockedFor: attempts.LockedFor);

        var result = @lock.Unlock(pin, now);
        state.Attempts = new PinAttempts(@lock.FailedAttempts, null, @lock.Lockouts, @lock.LockedAt, @lock.LockedFor);
        return result;
    }

    /// <summary>
    /// Replace the staff list with a fresh issue from the centre. An entry whose
    /// hash this device cannot verify is left out rather than trusted: it is a
    /// person who cannot unlock here, never one who unlocks with anything. The
    /// attempt counter is kept — a refresh is not a reason to forgive guesses.
    /// </summary>
    public static IReadOnlyList<string> Provision(DeviceState state, DeviceCredentialBundle bundle)
    {
        var skipped = new List<string>();
        var staff = new List<StaffCredential>();

        foreach (var entry in bundle.Credentials)
        {
            if (PinCredential.FromServerHash(entry.CredentialHash) is { } credential)
            {
                staff.Add(new StaffCredential(entry.RegistrarId, entry.DisplayName, entry.Role, credential));
            }
            else
            {
                skipped.Add(entry.DisplayName);
            }
        }

        state.Staff = staff;
        state.StaffIssuedAtUtc = bundle.IssuedAtUtc;
        return skipped;
    }
}
