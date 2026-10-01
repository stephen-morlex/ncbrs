using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Web;

namespace NCBRS.Services;

public enum RegistrarOnboardingResult
{
    Done,
    NotFound,
    NotPermitted,
    Refused,
    Conflict,
}

public sealed record RegistrarOnboardingOutcome(
    RegistrarOnboardingResult Result, Registrar? Registrar = null, string? Field = null, string? Detail = null);

/// <summary>What an account is to the registry right now.</summary>
public sealed record AccountStatus(Registrar? Registrar, bool Withdrawn, PendingAccount? Pending);

/// <summary>
/// Onboarding and withdrawing registrars (pilot readiness §1). Until this, only
/// the Development seed could bind an account, so a pilot district's staff
/// would have been refused everywhere.
///
/// Decided 2026-10-01: <b>an account declares itself</b> on first sign-in
/// (<see cref="RecordAsync"/>) and an officer binds it from a queue, so nobody
/// copies ids out of Keycloak and the registry holds no Keycloak admin
/// credential; and <b>district officers onboard for their own county</b>, the
/// Ministry anywhere.
///
/// Three limits on a binding, each closing a different way to misuse it:
/// - the facility is one the officer may act for (their county);
/// - the role is facility staff unless the Ministry binds it, because an
///   officer who could create officers could widen their own oversight;
/// - the role is one the account <em>holds in Keycloak</em>, and the account's
///   county group, if it has one, is the facility's. The registry records who
///   someone is here; it does not grant what the identity provider has not.
/// </summary>
public class RegistrarOnboardingService(NcbrsDbContext db, CurrentRegistrarService current, CountyLookup counties)
{
    private static readonly RegistrarRole[] FacilityStaff = [RegistrarRole.FacilityRegistrar, RegistrarRole.CommunityHealthWorker];

    /// <summary>
    /// Who this account is to the registry. An account with no registrar is
    /// recorded as pending, from what its own token says, so an officer can
    /// find it; a withdrawn one is not, since it was bound once and the trail
    /// keeps that binding.
    /// </summary>
    public async Task<AccountStatus> RecordAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return new AccountStatus(null, false, null);
        }

        var registrar = await db.Registrars.Include(entry => entry.Facility)
            .FirstOrDefaultAsync(entry => entry.ExternalSubjectId == subject, cancellationToken);
        if (registrar is not null)
        {
            return new AccountStatus(registrar, !registrar.IsActive, null);
        }

        var groups = KeycloakCounties.Of(user);
        var roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value)
            .Where(role => Enum.GetValues<RegistrarRole>().Any(known => NcbrsRoles.RealmRoleFor(known) == role))
            .Distinct()
            .Order();

        var pending = await db.PendingAccounts.FirstOrDefaultAsync(account => account.Subject == subject, cancellationToken);
        if (pending is null)
        {
            pending = new PendingAccount { Subject = subject, DisplayName = NameOf(user, subject) };
            db.PendingAccounts.Add(pending);
        }

        pending.DisplayName = NameOf(user, subject);
        pending.Username = user.FindFirstValue("preferred_username");
        pending.Email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email);
        pending.RealmRoles = string.Join(',', roles);
        pending.CountyCode = groups.Count == 1 ? groups[0] : null;
        pending.LastSeenAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return new AccountStatus(null, false, pending);
    }

    /// <summary>
    /// The accounts waiting for this officer: those placed in their county
    /// group. An account with no county group waits for the Ministry, which
    /// sees every account.
    /// </summary>
    public async Task<IReadOnlyList<PendingAccount>?> PendingForAsync(
        Registrar caller, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var query = db.PendingAccounts.AsNoTracking();

        if (!user.IsInRole(NcbrsRoles.MinistryAdmin))
        {
            var county = await counties.ForRegistrarAsync(caller, cancellationToken);
            if (county == AuditLog.Unknown)
            {
                return null;
            }

            query = query.Where(account => account.CountyCode == county);
        }

        return await query.OrderBy(account => account.FirstSeenAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<RegistrarOnboardingOutcome> BindAsync(
        BindRegistrarRequest request, Registrar caller, ClaimsPrincipal user, Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var pending = await db.PendingAccounts.FindAsync([request.PendingAccountId], cancellationToken);
        if (pending is null)
        {
            return new(RegistrarOnboardingResult.NotFound, Field: "data.pendingAccountId",
                Detail: "No account is waiting with that id. The person signs in to the registry once, and then appears here.");
        }

        var facility = await db.Facilities.FindAsync([request.FacilityId], cancellationToken);
        if (facility is null)
        {
            return new(RegistrarOnboardingResult.NotFound, Field: "data.facilityId", Detail: "No such facility.");
        }

        if (!await current.CanActForFacilityAsync(caller, facility.FacilityId, cancellationToken))
        {
            return new(RegistrarOnboardingResult.NotPermitted, Field: "data.facilityId",
                Detail: "You may onboard staff only for facilities in your own county.");
        }

        if (!user.IsInRole(NcbrsRoles.MinistryAdmin) && !FacilityStaff.Contains(request.Role))
        {
            return new(RegistrarOnboardingResult.NotPermitted, Field: "data.role",
                Detail: "A district officer onboards facility registrars and community health workers. Other roles are the Ministry's to give.");
        }

        var realmRole = NcbrsRoles.RealmRoleFor(request.Role);
        if (!pending.RealmRoles.Split(',').Contains(realmRole))
        {
            return new(RegistrarOnboardingResult.Refused, Field: "data.role",
                Detail: $"The account does not hold the {realmRole} role in Keycloak. Give it the role there, have the person sign in again, then bind it.");
        }

        var facilityCounty = await counties.ForFacilityAsync(facility.FacilityId, cancellationToken);
        if (pending.CountyCode is { } accountCounty && !string.Equals(accountCounty, facilityCounty, StringComparison.OrdinalIgnoreCase))
        {
            return new(RegistrarOnboardingResult.Refused, Field: "data.facilityId",
                Detail: $"The account is in county {accountCounty} in Keycloak, and this facility is in {facilityCounty}. Correct one or the other first.");
        }

        // A district officer's reporting is scoped by their county group; bound
        // without one, they would be refused every dashboard (CLAUDE.md, "Who reads what").
        if (request.Role == RegistrarRole.DistrictOfficer && pending.CountyCode is null)
        {
            return new(RegistrarOnboardingResult.Refused, Field: "data.role",
                Detail: "A district officer needs their county group in Keycloak before they are bound. Add it, have them sign in again, then bind.");
        }

        var registrar = new Registrar
        {
            FacilityId = facility.FacilityId,
            ExternalSubjectId = pending.Subject,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? pending.DisplayName : request.DisplayName.Trim(),
            Role = request.Role,
        };

        db.Registrars.Add(registrar);
        db.PendingAccounts.Remove(pending);
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Registrar),
            EntityId = registrar.RegistrarId.ToString(),
            Action = $"RegistrarBound:{request.Role}",
            CountyCode = facilityCounty,
            UserId = caller.RegistrarId,
            DeviceId = "web",
            TransactionId = transactionId,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new(RegistrarOnboardingResult.Conflict, Field: "data.pendingAccountId",
                Detail: "This account was bound by someone else a moment ago.");
        }

        registrar.Facility = facility;
        return new(RegistrarOnboardingResult.Done, registrar);
    }

    public async Task<RegistrarOnboardingOutcome> WithdrawAsync(
        Guid registrarId, WithdrawRegistrarRequest request, Registrar caller, ClaimsPrincipal user, Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var registrar = await db.Registrars.Include(entry => entry.Facility)
            .FirstOrDefaultAsync(entry => entry.RegistrarId == registrarId, cancellationToken);

        if (registrar is null || !await current.CanActForFacilityAsync(caller, registrar.FacilityId, cancellationToken))
        {
            // As the directory answers: another county's registrar is not
            // confirmed to exist.
            return new(RegistrarOnboardingResult.NotFound, Field: "registrarId", Detail: "No registrar with that id in your county.");
        }

        if (registrar.RegistrarId == caller.RegistrarId)
        {
            return new(RegistrarOnboardingResult.Refused, Field: "registrarId", Detail: "You cannot withdraw yourself; another officer must.");
        }

        if (!user.IsInRole(NcbrsRoles.MinistryAdmin) && !FacilityStaff.Contains(registrar.Role))
        {
            return new(RegistrarOnboardingResult.NotPermitted, Field: "registrarId",
                Detail: "A district officer withdraws facility staff. Withdrawing an officer is the Ministry's.");
        }

        if (!registrar.IsActive)
        {
            return new(RegistrarOnboardingResult.Conflict, Field: "registrarId",
                Detail: $"Already withdrawn on {registrar.WithdrawnAtUtc:yyyy-MM-dd}.");
        }

        registrar.WithdrawnAtUtc = DateTime.UtcNow;
        registrar.WithdrawnReason = request.Reason.Trim();
        registrar.WithdrawnByRegistrarId = caller.RegistrarId;

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Registrar),
            EntityId = registrar.RegistrarId.ToString(),
            Action = "RegistrarWithdrawn",
            CountyCode = await counties.ForFacilityAsync(registrar.FacilityId, cancellationToken),
            UserId = caller.RegistrarId,
            DeviceId = "web",
            TransactionId = transactionId,
        });

        await db.SaveChangesAsync(cancellationToken);
        return new(RegistrarOnboardingResult.Done, registrar);
    }

    private static string NameOf(ClaimsPrincipal user, string subject)
    {
        var name = user.FindFirstValue("name")
                   ?? string.Join(' ', new[] { user.FindFirstValue("given_name"), user.FindFirstValue("family_name") }
                       .Where(part => !string.IsNullOrWhiteSpace(part)));

        return !string.IsNullOrWhiteSpace(name) ? name.Trim()
            : user.FindFirstValue("preferred_username") ?? subject;
    }
}
