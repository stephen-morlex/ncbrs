using System.Text;

namespace NCBRS.Services;

/// <summary>One data row of a CSV file, by column name.</summary>
public sealed record CsvRow(int Line, IReadOnlyDictionary<string, string> Values)
{
    public string Get(string column) => Values.TryGetValue(column, out var value) ? value.Trim() : string.Empty;
}

/// <summary>
/// Reading the CSV files the Ministry and districts upload to set up a pilot
/// (facilities, then staff). They come from spreadsheets, so this accepts what
/// spreadsheets write: commas or semicolons (French and Arabic regional settings
/// save with semicolons), quoted fields, a byte-order mark, columns in any
/// order, and blank lines. One reader, so every import says the same things
/// about a file that is not what it should be.
/// </summary>
public static class CsvTable
{
    public const int MaxBytes = 256 * 1024;

    /// <param name="required">Columns the header must name, in lower case with underscores.</param>
    /// <param name="optional">Columns it may name, for the message when a required one is missing.</param>
    /// <param name="noun">What each row is, plural ("facilities"), for the messages.</param>
    public static (List<CsvRow> Rows, string? Problem) Read(
        string csv, IReadOnlyList<string> required, IReadOnlyList<string> optional, string noun, int maxRows)
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
        var missing = required.Where(column => !header.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            var also = optional.Count > 0 ? $" (and optionally {string.Join(", ", optional)})" : "";
            return ([], $"The first row must name the columns {string.Join(", ", required)}{also}. Missing: {string.Join(", ", missing)}.");
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
            return ([], $"The file has a header row but no {noun}.");
        }

        return rows.Count > maxRows ? ([], $"The file has {rows.Count} {noun}; at most {maxRows} at a time.") : (rows, null);
    }

    /// <summary>
    /// An enum as a person types it: any case, spaces or underscores between
    /// words ("Village health post", "offline_first"). Never a number, which
    /// would quietly pick a value by position.
    /// </summary>
    public static TEnum? ParseEnum<TEnum>(string value, string column, List<string> problems) where TEnum : struct, Enum
    {
        var compact = value.Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (compact.Length > 0 && !int.TryParse(compact, out _) && Enum.TryParse<TEnum>(compact, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        problems.Add($"The {column} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return null;
    }

    /// <summary>RFC 4180: separators between fields, quotes around a field that holds one or a quote.</summary>
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
