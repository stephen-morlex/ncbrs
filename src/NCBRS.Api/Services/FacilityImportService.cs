using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Validation;

namespace NCBRS.Services;

/// <summary>
/// Adding a district's facilities from one file (plan §17 item 38), so a pilot
/// is set up from the Ministry's own list rather than forty dialogs.
///
/// <para><b>Checked before anything is added, by the same checks as adding one</b>
/// (<see cref="FacilityOnboardingService.CheckAsync"/>), plus what only a file can
/// get wrong: the same name twice in one county, or one office code twice.</para>
///
/// <para><b>All or nothing.</b> A file is applied only if every row passes, and
/// in one transaction. A half-applied file leaves the Ministry working out
/// which rows exist, and re-running it would then refuse the ones that did as
/// duplicates. Each facility is still created by
/// <see cref="FacilityOnboardingService.CreateAsync"/>, so its county, its range
/// of numbers and its audit row are exactly as if it had been added by hand.</para>
/// </summary>
public class FacilityImportService(NcbrsDbContext db, FacilityOnboardingService onboarding)
{
    public const int MaxRows = 500;
    public const int MaxBytes = CsvTable.MaxBytes;

    private static readonly string[] Required = ["name", "tier", "connectivity", "area"];

    public async Task<ImportFacilitiesResponse> ImportAsync(
        ImportFacilitiesRequest request, Registrar actor, Guid? transactionId, CancellationToken cancellationToken = default)
    {
        var (rows, fileProblem) = CsvTable.Read(request.Csv, Required, ["office_code"], "facilities", MaxRows);
        if (fileProblem is not null)
        {
            return new ImportFacilitiesResponse(false, 0, 1, [new FacilityImportRow(1, "", null, null, null, [fileProblem], null, null, null)]);
        }

        var results = new List<(FacilityImportRow Row, CreateFacilityRequest? Request)>();
        var namesSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var codesSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var problems = new List<string>();
            var name = row.Get("name");
            var tier = CsvTable.ParseEnum<FacilityTier>(row.Get("tier"), "tier", problems);
            var connectivity = CsvTable.ParseEnum<ConnectivityProfile>(row.Get("connectivity"), "connectivity", problems);
            var officeCode = row.Get("office_code") is { Length: > 0 } typed ? OfficeCodes.Normalise(typed) : null;

            if (name.Length == 0)
            {
                problems.Add("A facility needs a name.");
            }
            else if (name.Length > 200)
            {
                problems.Add("The name is longer than 200 characters.");
            }

            if (officeCode is not null && !OfficeCodes.IsValid(officeCode))
            {
                problems.Add(OfficeCodes.Message);
            }

            var areaCode = row.Get("area");
            var area = areaCode.Length == 0
                ? null
                : await db.AdministrativeAreas.AsNoTracking()
                    .FirstOrDefaultAsync(entry => entry.Code == areaCode.ToUpper(), cancellationToken);

            if (areaCode.Length == 0)
            {
                problems.Add("Say where the facility is: the code of its county, payam, boma or village.");
            }
            else if (area is null)
            {
                problems.Add($"No administrative area has the code '{areaCode}'.");
            }

            CreateFacilityRequest? create = null;
            string? county = null;

            if (problems.Count == 0)
            {
                create = new CreateFacilityRequest
                {
                    Name = name,
                    Tier = tier!.Value,
                    ConnectivityProfile = connectivity!.Value,
                    AdministrativeAreaId = area!.AdministrativeAreaId,
                    OfficeCode = officeCode,
                };

                var (refusal, @checked) = await onboarding.CheckAsync(create, cancellationToken);
                if (refusal is not null)
                {
                    problems.Add(refusal.Detail!);
                }
                else
                {
                    county = @checked!.CountyCode;

                    // What only a file can get wrong: the same facility, or the
                    // same office code, twice.
                    if (namesSeen.TryGetValue($"{county}|{@checked.Name}", out var earlier))
                    {
                        problems.Add($"Line {earlier} already adds a facility called '{@checked.Name}' in {county}.");
                    }
                    else
                    {
                        namesSeen[$"{county}|{@checked.Name}"] = row.Line;
                    }

                    if (@checked.OfficeCode is { } code)
                    {
                        if (codesSeen.TryGetValue(code, out var first))
                        {
                            problems.Add($"Line {first} already uses office code {code}.");
                        }
                        else
                        {
                            codesSeen[code] = row.Line;
                        }
                    }
                }
            }

            results.Add((
                new FacilityImportRow(row.Line, name, area?.Name, county, officeCode, problems, null, null, null),
                problems.Count == 0 ? create : null));
        }

        var invalid = results.Count(result => result.Row.Problems.Count > 0);
        var valid = results.Count - invalid;

        if (!request.Apply || invalid > 0 || valid == 0)
        {
            return new ImportFacilitiesResponse(false, valid, invalid, [.. results.Select(result => result.Row)]);
        }

        // One transaction for the file (or a savepoint in the request's own;
        // see ImportTransaction).
        await using var transaction = await ImportTransaction.BeginAsync(db, cancellationToken);

        try
        {
            var applied = new List<FacilityImportRow>(results.Count);

            foreach (var (row, create) in results)
            {
                var outcome = await onboarding.CreateAsync(create!, actor, transactionId, cancellationToken);
                if (outcome.Result != FacilityOnboardingResult.Created)
                {
                    // Something changed between the check and now (another addition
                    // took the name or the code). Nothing from this file is kept.
                    await transaction.UndoAsync(cancellationToken);
                    return new ImportFacilitiesResponse(false, valid - 1, invalid + 1,
                        [.. results.Select(result => result.Row.Line == row.Line ? result.Row with { Problems = [outcome.Detail!] } : result.Row)]);
                }

                var facility = outcome.Facility!;
                applied.Add(row with { FacilityId = facility.FacilityId, BrnBlockStart = facility.BrnBlockStart, BrnBlockEnd = facility.BrnBlockEnd });
            }

            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Facility),
                EntityId = "import",
                Action = $"FacilitiesImported:{applied.Count}",
                CountyCode = applied.Select(row => row.CountyCode).Distinct().Count() == 1 ? applied[0].CountyCode! : AuditLog.Unknown,
                UserId = actor.RegistrarId,
                DeviceId = "web",
                TransactionId = transactionId,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ImportFacilitiesResponse(true, applied.Count, 0, applied);
        }
        catch (DbUpdateException)
        {
            // Another addition landed in the same moment (a range or code taken
            // under us). Every row of the file goes with it.
            await transaction.UndoAsync(cancellationToken);
            return new ImportFacilitiesResponse(false, valid, invalid,
                [.. results.Select(result => result.Row with
                {
                    Problems = ["Another facility was added while this file was being applied. Nothing from the file was added; check it again."],
                })]);
        }
    }
}
