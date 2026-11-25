using System.Text;
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
    public const int MaxBytes = 256 * 1024;

    private static readonly string[] Required = ["name", "tier", "connectivity", "area"];

    public async Task<ImportFacilitiesResponse> ImportAsync(
        ImportFacilitiesRequest request, Registrar actor, Guid? transactionId, CancellationToken cancellationToken = default)
    {
        var (rows, fileProblem) = Parse(request.Csv);
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
            var tier = ParseEnum<FacilityTier>(row.Get("tier"), "tier", problems);
            var connectivity = ParseEnum<ConnectivityProfile>(row.Get("connectivity"), "connectivity", problems);
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

        // One transaction for the file. A request carrying a transaction id
        // already runs inside the idempotency filter's, so a savepoint stands
        // in for our own there.
        var ambient = db.Database.CurrentTransaction;
        var own = ambient is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var savepoint = $"import_{Guid.NewGuid():N}";
        if (ambient is not null)
        {
            await ambient.CreateSavepointAsync(savepoint, cancellationToken);
        }

        async Task UndoAsync()
        {
            if (own is not null)
            {
                await own.RollbackAsync(cancellationToken);
            }
            else
            {
                await ambient!.RollbackToSavepointAsync(savepoint, cancellationToken);
            }

            db.ChangeTracker.Clear();
        }

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
                    await UndoAsync();
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

            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }

            return new ImportFacilitiesResponse(true, applied.Count, 0, applied);
        }
        catch (DbUpdateException)
        {
            // Another addition landed in the same moment (a range or code taken
            // under us). Every row of the file goes with it.
            await UndoAsync();
            return new ImportFacilitiesResponse(false, valid, invalid,
                [.. results.Select(result => result.Row with
                {
                    Problems = ["Another facility was added while this file was being applied. Nothing from the file was added; check it again."],
                })]);
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }
    }

    private static TEnum? ParseEnum<TEnum>(string value, string column, List<string> problems) where TEnum : struct, Enum
    {
        // "Village health post" as well as "VillageHealthPost": the file is typed by people.
        var compact = value.Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (compact.Length > 0 && !int.TryParse(compact, out _) && Enum.TryParse<TEnum>(compact, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        problems.Add($"The {column} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return null;
    }

    // --- the file ------------------------------------------------------------------------------

    private sealed record CsvRow(int Line, Dictionary<string, string> Values)
    {
        public string Get(string column) => Values.TryGetValue(column, out var value) ? value.Trim() : string.Empty;
    }

    private static (List<CsvRow> Rows, string? Problem) Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return ([], "The file is empty.");
        }

        if (Encoding.UTF8.GetByteCount(csv) > MaxBytes)
        {
            return ([], $"The file is larger than {MaxBytes / 1024} KB.");
        }

        var lines = ReadRecords(csv.TrimStart('﻿'));
        if (lines.Count == 0)
        {
            return ([], "The file is empty.");
        }

        var header = lines[0].Fields.Select(field => field.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
        var missing = Required.Where(column => !header.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            return ([], $"The first row must name the columns {string.Join(", ", Required)} (and optionally office_code). Missing: {string.Join(", ", missing)}.");
        }

        var rows = lines.Skip(1)
            .Where(record => record.Fields.Any(field => !string.IsNullOrWhiteSpace(field)))
            .Select(record => new CsvRow(
                record.Line,
                header.Select((column, index) => (column, value: index < record.Fields.Count ? record.Fields[index] : ""))
                    .GroupBy(pair => pair.column)
                    .ToDictionary(group => group.Key, group => group.First().value)))
            .ToList();

        if (rows.Count == 0)
        {
            return ([], "The file has a header row but no facilities.");
        }

        return rows.Count > MaxRows ? ([], $"The file has {rows.Count} facilities; at most {MaxRows} at a time.") : (rows, null);
    }

    /// <summary>RFC 4180: commas or semicolons between fields, quotes around a field that holds either or a quote.</summary>
    private static List<(int Line, List<string> Fields)> ReadRecords(string text)
    {
        var separator = DetectSeparator(text);
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var recordLine = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                records.Add((recordLine, fields));
                fields = [];
                line++;
                recordLine = line;
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add((recordLine, fields));
        }

        return records;
    }

    /// <summary>Spreadsheets set to French or Arabic regional settings save CSV with semicolons.</summary>
    private static char DetectSeparator(string text)
    {
        var header = text.Split('\n', 2)[0];
        return header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
    }
}
