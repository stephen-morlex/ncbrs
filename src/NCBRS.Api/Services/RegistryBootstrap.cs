using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Web;

namespace NCBRS.Services;

/// <summary>What the bootstrap is told, from the <c>Bootstrap</c> configuration section.</summary>
public class RegistryBootstrapOptions
{
    public const string SectionName = "Bootstrap";

    /// <summary>
    /// The Keycloak username of the first system admin. Left out, the
    /// bootstrap loads the geography and binds nobody.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>The Ministry facility they are bound at: found by name in the area's county, or created.</summary>
    public string? FacilityName { get; set; }

    /// <summary>Where that facility is, by p-code (<c>SS0101</c>): a county, or an area within one.</summary>
    public string? Area { get; set; }

    public FacilityTier Tier { get; set; } = FacilityTier.Hospital;

    public ConnectivityProfile Connectivity { get; set; } = ConnectivityProfile.AlwaysOn;

    /// <summary>Optional, and set once for good, as for any facility.</summary>
    public string? OfficeCode { get; set; }
}

public sealed record RegistryBootstrapOutcome(bool Succeeded, IReadOnlyList<string> Messages);

/// <summary>
/// Makes a new, empty registry usable. It is run once by whoever deploys it, as
/// <c>NCBRS.Api bootstrap</c> with the Api's own configuration, after the
/// migrations.
///
/// Two things nothing else can do on an empty registry:
/// <list type="bullet">
/// <item><b>Load the official geography.</b> The seeder ran only on
/// Development startup, so a production registry had no county to place a
/// facility in. It is idempotent, so the bootstrap runs it every time.</item>
/// <item><b>Bind the first system admin.</b> Binding an account and creating
/// a facility each need a system admin already bound, so on an empty registry
/// neither could ever happen. The account still declares itself, by signing
/// in to the web site once, exactly as every other account does. The
/// bootstrap names it by username and takes its subject from that sign-in,
/// so no id is copied out of Keycloak.</item>
/// </list>
///
/// <b>Refused once an active system admin exists.</b> From then on, accounts
/// are bound from the web site, where the limits on who may bind whom apply.
/// It stays available if every system admin has been withdrawn, which is the
/// only other way to be locked out. Whoever can run it already holds the
/// database credentials, and the audit trail records each use.
/// </summary>
public class RegistryBootstrap(NcbrsDbContext db, FacilityOnboardingService facilities, CountyLookup counties)
{
    /// <summary>The command-line verb that runs this instead of the web host.</summary>
    public const string Verb = "bootstrap";

    /// <summary>The device id its audit rows carry: no device was involved.</summary>
    public const string Channel = "bootstrap";

    public async Task<RegistryBootstrapOutcome> RunAsync(RegistryBootstrapOptions options, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            return Refused(messages, $"The database is missing {pending.Count} migration(s), starting with {pending[0]}. Apply the migrations first.");
        }

        await AdministrativeAreaSeeder.SeedAsync(db, cancellationToken);
        var areas = await db.AdministrativeAreas.CountAsync(cancellationToken);
        messages.Add($"Official geography loaded: {areas} administrative areas.");

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            messages.Add("No Bootstrap:Username was given, so no system admin was bound.");
            return new(true, messages);
        }

        return await BindFirstSystemAdminAsync(options, messages, cancellationToken);
    }

    private async Task<RegistryBootstrapOutcome> BindFirstSystemAdminAsync(
        RegistryBootstrapOptions options, List<string> messages, CancellationToken cancellationToken)
    {
        var existing = await db.Registrars.AsNoTracking()
            .Where(registrar => registrar.Role == RegistrarRole.SystemAdmin && registrar.WithdrawnAtUtc == null)
            .Select(registrar => registrar.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return Refused(messages,
                $"A system admin is already bound ({existing}). Further accounts are bound on the web site: Registrars, Waiting to be added.");
        }

        var username = options.Username!.Trim();
        var account = await db.PendingAccounts
            .FirstOrDefaultAsync(entry => entry.Username != null && entry.Username.ToLower() == username.ToLower(), cancellationToken);
        if (account is null)
        {
            return Refused(messages,
                $"No account named '{username}' has signed in to the registry yet. Have them sign in to the web site once, then run this again.");
        }

        if (!account.RealmRoles.Split(',').Contains(NcbrsRoles.SystemAdmin))
        {
            return Refused(messages,
                $"'{username}' does not hold the {NcbrsRoles.SystemAdmin} role in Keycloak. Give it the role, have them sign in again, then run this again.");
        }

        if (string.IsNullOrWhiteSpace(options.FacilityName) || string.IsNullOrWhiteSpace(options.Area))
        {
            return Refused(messages, "Bootstrap:FacilityName and Bootstrap:Area (a p-code, such as SS0101) are both needed.");
        }

        var areaCode = options.Area.Trim();
        var area = await db.AdministrativeAreas.AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Code == areaCode, cancellationToken);
        if (area is null)
        {
            return Refused(messages, $"No administrative area has the code '{areaCode}'.");
        }

        // The rule the web site applies to any binding: an account in a
        // county group is bound in that county.
        var county = await counties.ForAreaAsync(area.AdministrativeAreaId, cancellationToken);
        if (account.CountyCode is { } accountCounty && county is not null
            && !string.Equals(accountCounty, county, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(messages,
                $"'{username}' is in county {accountCounty} in Keycloak, and {areaCode} is in {county}. A Ministry account normally has no county group.");
        }

        // Its RegistrarId is known now, so the facility's creation is
        // credited to the admin it is being created for.
        var admin = new Registrar
        {
            ExternalSubjectId = account.Subject,
            DisplayName = account.DisplayName,
            Role = RegistrarRole.SystemAdmin,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var name = options.FacilityName.Trim();
        var facility = county is null
            ? null
            : await db.Facilities.FirstOrDefaultAsync(
                entry => entry.CountyCode == county && entry.Name.ToLower() == name.ToLower(), cancellationToken);

        if (facility is null)
        {
            var created = await facilities.CreateAsync(new CreateFacilityRequest
            {
                Name = name,
                Tier = options.Tier,
                ConnectivityProfile = options.Connectivity,
                AdministrativeAreaId = area.AdministrativeAreaId,
                OfficeCode = string.IsNullOrWhiteSpace(options.OfficeCode) ? null : options.OfficeCode,
            }, admin, transactionId: null, cancellationToken);

            if (created.Result != FacilityOnboardingResult.Created)
            {
                return Refused(messages, created.Detail ?? $"The facility could not be created ({created.Result}).");
            }

            facility = created.Facility!;
            messages.Add($"Created facility '{facility.Name}' in {facility.CountyCode}, numbers {facility.BrnBlockStart}-{facility.BrnBlockEnd}.");
        }
        else
        {
            messages.Add($"Using the existing facility '{facility.Name}' in {facility.CountyCode}.");
        }

        admin.FacilityId = facility.FacilityId;
        db.Registrars.Add(admin);
        db.PendingAccounts.Remove(account);
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Registrar),
            EntityId = admin.RegistrarId.ToString(),
            Action = $"SystemAdminBootstrapped:{username}",
            CountyCode = facility.CountyCode,
            UserId = admin.RegistrarId,
            DeviceId = Channel,
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        messages.Add($"Bound '{username}' ({admin.DisplayName}) as system admin at '{facility.Name}'. They can now sign in and work.");
        return new(true, messages);
    }

    private static RegistryBootstrapOutcome Refused(List<string> messages, string reason)
    {
        messages.Add(reason);
        return new(false, messages);
    }
}
