using System.Text.Json;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Http;

/// <summary>
/// Materializes a positional <see cref="ReportRecord"/> from one JSON record element (ADR D61) —
/// for each declared schema column, reads the JSON field at the configured dotted path (the
/// <c>fieldMap</c> entry for that column name, or the column name itself when unmapped), the same
/// "match by declared schema name" pattern <c>AdoConfigProperties.MaterializeReportRecord</c> /
/// <c>CsvReportRecordMaterializer</c> / <c>ParquetReportRecordMaterializer</c> already established.
/// </summary>
internal static class HttpReportRecordMaterializer
{
    /// <summary>Builds a <see cref="ReportRecord"/> aligned to <paramref name="schema"/> from one record element.</summary>
    /// <param name="record">The parsed JSON record.</param>
    /// <param name="schema">The report's declared output schema.</param>
    /// <param name="fieldMap">Optional report-column-name to dotted-JSON-field-path overrides.</param>
    public static ReportRecord Materialize(JsonElement record, ReportSchema schema, IReadOnlyDictionary<string, string>? fieldMap)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            ReportColumn column = schema.Columns[i];
            string path = fieldMap is not null && fieldMap.TryGetValue(column.Name, out string? mapped) ? mapped : column.Name;
            values[i] = JsonRecords.TryGetField(record, path, out JsonElement value) ? ConvertValue(value) : null;
        }

        return new ReportRecord(schema, values);
    }

    private static object? ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out long l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        // A nested object/array at a leaf field is unusual for a mapped column; kept as raw JSON
        // text rather than silently dropped, matching the writer edge's "never fabricate" rule (D36).
        _ => value.GetRawText(),
    };
}
