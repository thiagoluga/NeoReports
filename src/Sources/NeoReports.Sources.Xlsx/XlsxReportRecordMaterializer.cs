using System.Globalization;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// Materializes a positional <see cref="ReportRecord"/> from an XLSX row for the dynamic
/// (config-driven) path (ADR D59, <c>type: "xlsx"</c>) — the XLSX-source analog of
/// <c>NeoReports.Sources.Common.AdoConfigProperties.MaterializeReportRecord</c>'s "match by declared
/// schema column name" pattern, letting a report declare a reordered subset of the file's real
/// columns. When the sheet has no header (<see cref="XlsxReaderOptions.Header"/> disabled), name
/// matching is impossible, so columns align positionally to the declared schema order instead.
/// </summary>
internal static class XlsxReportRecordMaterializer
{
    /// <summary>Materializes one row against <paramref name="schema"/>.</summary>
    /// <param name="ordinalByName">Header column ordinal by name (case-insensitive); empty when the sheet has no header.</param>
    /// <param name="row">The row's raw cell values.</param>
    /// <param name="schema">The report's declared output schema.</param>
    public static ReportRecord Materialize(IReadOnlyDictionary<string, int> ordinalByName, object?[] row, ReportSchema schema)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            int ordinal = ordinalByName.Count > 0
                ? (ordinalByName.TryGetValue(schema.Columns[i].Name, out var found) ? found : -1)
                : i;
            values[i] = ordinal >= 0 && ordinal < row.Length && row[ordinal] is not null
                ? ConvertField(row[ordinal]!, schema.Columns[i].Type)
                : null;
        }

        return new ReportRecord(schema, values);
    }

    // A cell already carries a native value from XlsxRowReader — always one of double, string, bool,
    // or DateTime (its own contract, enforced by ReadCellValue), never long/int/Guid the way a DB
    // reader's values might be. Unlike CSV, there's no text to parse, only a possible mismatch between
    // that native type and the column's own declared ColumnType (e.g. a numeric cell feeding a Uuid
    // column, because the sheet was hand-authored rather than produced by this project's own writer).
    // A value that doesn't convert falls back to its raw ToString() rather than throwing mid-batch, the
    // same "don't crash on a malformed value" philosophy CsvReportRecordMaterializer uses.
    private static object? ConvertField(object raw, ColumnType type)
    {
        try
        {
            return type switch
            {
                ColumnType.Integer => raw is double d ? checked((long)d) : long.Parse(raw.ToString()!, CultureInfo.InvariantCulture),
                ColumnType.Decimal or ColumnType.Money => Convert.ToDecimal(raw, CultureInfo.InvariantCulture),
                ColumnType.Boolean => raw is bool b ? b : bool.Parse(raw.ToString()!),
                ColumnType.Uuid => Guid.Parse(raw.ToString()!),
                ColumnType.Time => raw is DateTime dt ? dt.TimeOfDay : TimeSpan.Parse(raw.ToString()!, CultureInfo.InvariantCulture),
                ColumnType.Date or ColumnType.DateTime or ColumnType.Timestamp => raw is DateTime rdt
                    ? rdt
                    : DateTime.Parse(raw.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => raw.ToString(), // String, Json, Binary — kept as text
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            // FormatException covers unparseable text; OverflowException covers a numeric value that
            // parses but doesn't fit the target type; InvalidCastException covers Convert.To* rejecting
            // an incompatible native type (e.g. a string cell where a number was expected).
            return raw.ToString();
        }
    }
}
