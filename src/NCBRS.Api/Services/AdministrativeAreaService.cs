using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

public enum AreaCreationResult
{
    Created,
    ParentNotFound,
    NotInACounty,
    WrongLevel,
    NameTaken,
}

public sealed record AreaCreationOutcome(AreaCreationResult Result, AdministrativeArea? Area = null, string? Detail = null);

/// <summary>
/// Adding an area below a county (plan §17 item 31). The official dataset stops
/// at the payam, so until this a village post could only be placed at its
/// payam, and the location the tree was built to hold was lost.
///
/// <para><b>The code is given, never typed</b> (<see cref="AdministrativeLevels.LocalCode"/>):
/// codes are what audit rows, the reporting projection and DHIS2 maps hold, so a
/// typed one that collided with an official p-code published later would point
/// two places at one name.</para>
///
/// <para><b>States and counties are never added here.</b> They are the official
/// geography, and county is the scope every officer acts in: one added by hand
/// would be a scope no Keycloak group names.</para>
/// </summary>
public partial class AdministrativeAreaService(NcbrsDbContext db, CountyLookup counties)
{
    private const int Attempts = 3;

    public async Task<AreaCreationOutcome> CreateAsync(
        CreateAdministrativeAreaRequest request, Registrar actor, Guid? transactionId, CancellationToken cancellationToken = default)
    {
        var parent = await db.AdministrativeAreas.AsNoTracking()
            .FirstOrDefaultAsync(area => area.AdministrativeAreaId == request.ParentId, cancellationToken);

        if (parent is null)
        {
            return new(AreaCreationResult.ParentNotFound, Detail: "No such administrative area.");
        }

        // Inside a county, or the new area would have no county to be scoped by.
        var county = await counties.ForAreaAsync(parent.AdministrativeAreaId, cancellationToken);
        if (county is null || AdministrativeLevels.Tier(parent.Level) < AdministrativeLevels.Tier(AdministrativeLevel.County))
        {
            return new(AreaCreationResult.NotInACounty,
                Detail: $"'{parent.Name}' is a {parent.Level}. Areas are added inside a county.");
        }

        if (!AdministrativeLevels.IsCreatable(request.Level) || !AdministrativeLevels.CanContain(parent.Level, request.Level))
        {
            return new(AreaCreationResult.WrongLevel,
                Detail: $"A {request.Level} cannot sit inside a {parent.Level}.");
        }

        if (AdministrativeLevels.MixesRuralAndUrban(parent.Level, request.Level))
        {
            return new(AreaCreationResult.WrongLevel,
                Detail: $"A {request.Level} cannot sit inside a {parent.Level}: payams contain bomas, and blocks contain quarters.");
        }

        var name = Whitespace().Replace(request.Name.Trim(), " ");

        // Among its siblings only: two villages called Gumbo in different
        // payams are two places, two in one payam are one place recorded twice.
        var siblings = await db.AdministrativeAreas.AsNoTracking()
            .Where(area => area.ParentId == parent.AdministrativeAreaId)
            .Select(area => new { area.Name, area.Code })
            .ToListAsync(cancellationToken);

        if (siblings.Any(sibling => string.Equals(sibling.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return new(AreaCreationResult.NameTaken,
                Detail: $"{parent.Name} already has an area called '{name}'.");
        }

        for (var attempt = 1; ; attempt++)
        {
            var code = await NextCodeAsync(parent.Code, request.Level, cancellationToken);
            var area = new AdministrativeArea
            {
                Name = name,
                Level = request.Level,
                ParentId = parent.AdministrativeAreaId,
                Code = code,
            };

            db.AdministrativeAreas.Add(area);
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(AdministrativeArea),
                EntityId = area.AdministrativeAreaId.ToString(),
                Action = $"AdministrativeAreaCreated:{code}",
                CountyCode = county,
                UserId = actor.RegistrarId,
                DeviceId = "web",
                TransactionId = transactionId,
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new(AreaCreationResult.Created, area);
            }
            catch (DbUpdateException) when (attempt < Attempts)
            {
                // Another addition took this code first (codes are unique): count again.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// The next free number for this level under this parent. Counted from the
    /// codes already given, not the number of rows, so a code is never reused.
    /// </summary>
    private async Task<string> NextCodeAsync(string parentCode, AdministrativeLevel level, CancellationToken cancellationToken)
    {
        var prefix = AdministrativeLevels.LocalCode(parentCode, level, 0)[..^2];
        var taken = await db.AdministrativeAreas.AsNoTracking()
            .Where(area => area.Code.StartsWith(prefix))
            .Select(area => area.Code)
            .ToListAsync(cancellationToken);

        var highest = taken
            .Select(code => code[prefix.Length..])
            .Select(rest => int.TryParse(rest, out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();

        return AdministrativeLevels.LocalCode(parentCode, level, highest + 1);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
