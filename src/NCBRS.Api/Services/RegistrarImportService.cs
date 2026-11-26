using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Binding a district's staff from one file (plan §17 item 38). On a training
/// day everyone signs in once, which puts their accounts in the waiting queue;
/// this binds them all to their facilities and roles in one go instead of one
/// row at a time.
///
/// <para><b>The same limits as binding one</b>
/// (<see cref="RegistrarOnboardingService.CheckBindAsync"/>): the officer's own
/// county, facility staff only unless the Ministry binds, a role the account
/// holds in Keycloak, a county group that agrees. A file grants nothing a
/// single binding would not.</para>
///
/// <para><b>Accounts are named by username</b>, the name people sign in with
/// and the one a district's list already holds, never by Keycloak subject,
/// which the queue does not publish.</para>
///
/// <para><b>Checked first, applied whole.</b> Each person is bound by
/// <see cref="RegistrarOnboardingService.BindAsync"/>, so the audit trail is
/// exactly as if they were bound by hand.</para>
/// </summary>
public class RegistrarImportService(NcbrsDbContext db, RegistrarOnboardingService onboarding, CurrentRegistrarService current)
{
    public const int MaxRows = 500;

    private static readonly string[] Required = ["username", "facility", "role"];

    public async Task<ImportRegistrarsResponse> ImportAsync(
        ImportRegistrarsRequest request, Registrar caller, ClaimsPrincipal user, Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var (rows, fileProblem) = CsvTable.Read(request.Csv, Required, ["display_name"], "people", MaxRows);
        if (fileProblem is not null)
        {
            return new ImportRegistrarsResponse(false, 0, 1, [new RegistrarImportRow(1, "", null, null, null, [fileProblem], null)]);
        }

        var results = new List<(RegistrarImportRow Row, BindRegistrarRequest? Bind)>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var counties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var problems = new List<string>();
            var username = row.Get("username");
            var displayName = row.Get("display_name") is { Length: > 0 } named ? named : null;
            var role = CsvTable.ParseEnum<RegistrarRole>(row.Get("role"), "role", problems);
            var facility = await FacilityAsync(row.Get("facility"), caller, problems, cancellationToken);

            PendingAccount? pending = null;
            if (username.Length == 0)
            {
                problems.Add("Give the person's username, the name they sign in with.");
            }
            else if (seen.TryGetValue(username, out var earlier))
            {
                problems.Add($"Line {earlier} already binds '{username}'.");
            }
            else
            {
                seen[username] = row.Line;
                var lowered = username.ToLower();
                pending = await db.PendingAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(account => account.Username != null && account.Username.ToLower() == lowered, cancellationToken);

                if (pending is null)
                {
                    problems.Add($"No account is waiting for '{username}'. The person signs in to the registry once first; if they have, they may already be bound.");
                }
            }

            BindRegistrarRequest? bind = null;
            if (problems.Count == 0)
            {
                bind = new BindRegistrarRequest
                {
                    PendingAccountId = pending!.PendingAccountId,
                    FacilityId = facility!.FacilityId,
                    Role = role!.Value,
                    DisplayName = displayName,
                };

                var (refusal, @checked) = await onboarding.CheckBindAsync(bind, caller, user, cancellationToken);
                if (refusal is not null)
                {
                    problems.Add(refusal.Detail!);
                    bind = null;
                }
                else
                {
                    counties.Add(@checked!.FacilityCounty);
                }
            }

            results.Add((
                new RegistrarImportRow(row.Line, username, displayName ?? pending?.DisplayName, facility?.Name, role, problems, null),
                bind));
        }

        var invalid = results.Count(result => result.Row.Problems.Count > 0);
        var valid = results.Count - invalid;

        if (!request.Apply || invalid > 0 || valid == 0)
        {
            return new ImportRegistrarsResponse(false, valid, invalid, [.. results.Select(result => result.Row)]);
        }

        await using var transaction = await ImportTransaction.BeginAsync(db, cancellationToken);
        var applied = new List<RegistrarImportRow>(results.Count);

        foreach (var (row, bind) in results)
        {
            var outcome = await onboarding.BindAsync(bind!, caller, user, transactionId, cancellationToken);
            if (outcome.Result != RegistrarOnboardingResult.Done)
            {
                // Bound by someone else, or changed in Keycloak, since the
                // check. Nobody from this file is bound.
                await transaction.UndoAsync(cancellationToken);
                return new ImportRegistrarsResponse(false, valid - 1, invalid + 1,
                    [.. results.Select(result => result.Row.Line == row.Line ? result.Row with { Problems = [outcome.Detail!] } : result.Row)]);
            }

            applied.Add(row with { RegistrarId = outcome.Registrar!.RegistrarId });
        }

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Registrar),
            EntityId = "import",
            Action = $"RegistrarsImported:{applied.Count}",
            CountyCode = counties.Count == 1 ? counties.First() : AuditLog.Unknown,
            UserId = caller.RegistrarId,
            DeviceId = "web",
            TransactionId = transactionId,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ImportRegistrarsResponse(true, applied.Count, 0, applied);
    }

    /// <summary>
    /// The facility a row names: its office code, which is unique, or its name.
    /// A name can recur in other counties, so a name that matches more than one
    /// facility the caller may act for asks for the office code instead.
    /// </summary>
    private async Task<Facility?> FacilityAsync(
        string named, Registrar caller, List<string> problems, CancellationToken cancellationToken)
    {
        if (named.Length == 0)
        {
            problems.Add("Say which facility: its office code, or its name.");
            return null;
        }

        var upper = named.ToUpper();
        var byCode = await db.Facilities.AsNoTracking().FirstOrDefaultAsync(facility => facility.OfficeCode == upper, cancellationToken);
        if (byCode is not null)
        {
            return byCode;
        }

        var lowered = named.ToLower();
        var byName = await db.Facilities.AsNoTracking().Where(facility => facility.Name.ToLower() == lowered).ToListAsync(cancellationToken);

        var mine = new List<Facility>();
        foreach (var facility in byName)
        {
            if (await current.CanActForFacilityAsync(caller, facility.FacilityId, cancellationToken))
            {
                mine.Add(facility);
            }
        }

        switch (mine.Count)
        {
            case 1:
                return mine[0];
            case > 1:
                problems.Add($"{mine.Count} facilities are called '{named}'. Use the office code of the one you mean.");
                return null;
            default:
                problems.Add(byName.Count > 0
                    ? $"'{named}' is outside your county."
                    : $"No facility has the office code or name '{named}'.");
                return null;
        }
    }
}
