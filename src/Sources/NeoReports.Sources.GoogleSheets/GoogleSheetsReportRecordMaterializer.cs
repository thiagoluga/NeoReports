using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// Materializes a positional <see cref="ReportRecord"/> from a Google Sheets row for the dynamic
/// (config-driven) path (ADR D66, <c>type: "googlesheets"</c>) — the Sheets-source analog of
/// <c>NeoReports.Sources.Csv.CsvReportRecordMaterializer</c>'s "match by declared schema column name
/// against the header" pattern.
/// </summary>
internal static class GoogleSheetsReportRecordMaterializer
{
    /// <summary>Materializes one row against <paramref name="schema"/>.</summary>
    /// <param name="ordinalByName">Header column ordinal by name (case-insensitive).</param>
    /// <param name="row">The row's raw cell values.</param>
    /// <param name="schema">The report's declared output schema.</param>
    public static ReportRecord Materialize(IReadOnlyDictionary<string, int> ordinalByName, JsonElement[] row, ReportSchema schema)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            int ordinal = ordinalByName.TryGetValue(schema.Columns[i].Name, out var found) ? found : -1;
            values[i] = ordinal >= 0 && ordinal < row.Length
                ? ConvertField(row[ordinal], schema.Columns[i].Type)
                : null;
        }

        return new ReportRecord(schema, values);
    }

    // Mirrors CsvReportRecordMaterializer.ConvertField's decline-rather-than-throw stance for a
    // value that doesn't parse as its declared column type.
    private static object? ConvertField(JsonElement cell, ColumnType type)
    {
        if (type is ColumnType.String or ColumnType.Json or ColumnType.Binary)
            return GoogleSheetsCellText.Text(cell);

        if (type is ColumnType.Date or ColumnType.DateTime or ColumnType.Timestamp && cell.ValueKind == JsonValueKind.Number)
            return GoogleSheetsCellText.Epoch.AddDays(cell.GetDouble());

        if (type == ColumnType.Time && cell.ValueKind == JsonValueKind.Number)
            return TimeSpan.FromDays(cell.GetDouble());

        string raw = GoogleSheetsCellText.Text(cell);
        if (cell.ValueKind != JsonValueKind.Number && string.IsNullOrEmpty(raw))
            return null;

        try
        {
            return type switch
            {
                ColumnType.Integer => cell.ValueKind == JsonValueKind.Number ? (long)cell.GetDouble() : long.Parse(raw, CultureInfo.InvariantCulture),
                ColumnType.Decimal or ColumnType.Money => cell.ValueKind == JsonValueKind.Number ? (decimal)cell.GetDouble() : decimal.Parse(raw, CultureInfo.InvariantCulture),
                ColumnType.Boolean => cell.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => bool.Parse(raw) },
                ColumnType.Uuid => Guid.Parse(raw),
                ColumnType.Date or ColumnType.DateTime or ColumnType.Timestamp => DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ColumnType.Time => TimeSpan.Parse(raw, CultureInfo.InvariantCulture),
                _ => raw,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return raw;
        }
    }
}
