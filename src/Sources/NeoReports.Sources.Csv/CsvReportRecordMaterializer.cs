using System.Globalization;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Csv;

/// <summary>
/// Materializes a positional <see cref="ReportRecord"/> from a CSV row for the dynamic
/// (config-driven) path (ADR D58, <c>type: "csv"</c>) — the CSV-source analog of
/// <c>NeoReports.Sources.Common.AdoConfigProperties.MaterializeReportRecord</c>'s "match by declared
/// schema column name" pattern, letting a report declare a reordered subset of the file's real
/// columns. When the CSV has no header (<see cref="CsvReaderOptions.Header"/> disabled), name
/// matching is impossible, so columns align positionally to the declared schema order instead.
/// </summary>
internal static class CsvReportRecordMaterializer
{
    /// <summary>Materializes one row against <paramref name="schema"/>.</summary>
    /// <param name="ordinalByName">Header column ordinal by name (case-insensitive); empty when the file has no header.</param>
    /// <param name="row">The row's raw string fields.</param>
    /// <param name="schema">The report's declared output schema.</param>
    public static ReportRecord Materialize(IReadOnlyDictionary<string, int> ordinalByName, string[] row, ReportSchema schema)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            int ordinal = ordinalByName.Count > 0
                ? (ordinalByName.TryGetValue(schema.Columns[i].Name, out var found) ? found : -1)
                : i;
            values[i] = ordinal >= 0 && ordinal < row.Length && !string.IsNullOrEmpty(row[ordinal])
                ? ConvertField(row[ordinal], schema.Columns[i].Type)
                : null;
        }

        return new ReportRecord(schema, values);
    }

    // Every CSV field is inherently text — unlike an ADO source, there's no native driver type to
    // extract, so this converts to the column's own declared ColumnType instead of leaving every
    // value as a raw string (which would work for CSV-to-CSV round-tripping by coincidence, but would
    // behave inconsistently with the ADO family everywhere else a properly-typed value matters, e.g.
    // a numeric-aware writer cell type or a downstream sort/filter). A field that doesn't parse as
    // its declared type falls back to the raw text rather than throwing mid-batch — a hand-authored
    // CSV can contain a malformed value in a way a real database driver never hands back.
    private static object? ConvertField(string raw, ColumnType type)
    {
        try
        {
            return type switch
            {
                ColumnType.Integer => long.Parse(raw, CultureInfo.InvariantCulture),
                ColumnType.Decimal or ColumnType.Money => decimal.Parse(raw, CultureInfo.InvariantCulture),
                ColumnType.Boolean => bool.Parse(raw),
                ColumnType.Uuid => Guid.Parse(raw),
                ColumnType.Time => TimeSpan.Parse(raw, CultureInfo.InvariantCulture),
                ColumnType.Date or ColumnType.DateTime or ColumnType.Timestamp =>
                    DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => raw, // String, Json, Binary — kept as text
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // FormatException covers unparseable text; OverflowException covers a numeric/TimeSpan
            // value that parses but doesn't fit the target type (e.g. an Integer field bigger than
            // long.MaxValue) — long.Parse/decimal.Parse/TimeSpan.Parse throw the latter, not the
            // former, for an out-of-range value, so both need to fall back the same way.
            return raw;
        }
    }
}
