using System.Globalization;
using NeoReports.Abstractions;
using Parquet.Schema;

namespace NeoReports.Sources.Parquet;

/// <summary>
/// Materializes positional <see cref="ReportRecord"/>s from a Parquet row group's untyped rows for the
/// dynamic (config-driven) path (ADR D60, <c>type: "parquet"</c>) — the Parquet analog of
/// <c>NeoReports.Sources.Xlsx.XlsxReportRecordMaterializer</c> and
/// <c>NeoReports.Sources.Common.AdoConfigProperties.MaterializeReportRecord</c>'s "match by declared
/// schema column name" pattern, letting a report declare a reordered subset of the file's real columns.
/// </summary>
/// <remarks>
/// <see cref="ReportRecord"/>/<see cref="ReportSchema"/> are inherently flat (every source in this
/// project — ADO, CSV, XLSX — reads flat rows into them), so this materializer only resolves
/// top-level, scalar Parquet columns. A declared column whose name matches a <b>nested or repeated</b>
/// Parquet field (a list or struct) resolves the same way a genuinely absent column does — null for
/// every row — because <see cref="ParquetSchema.DataFields"/> exposes only leaf field names for those,
/// never the composite top-level name the row dictionaries are actually keyed by. This is a
/// pre-existing constraint of the pipeline's row model, not something Parquet-specific to fix here;
/// stated plainly rather than silently, matching this project's own "honest capability gaps" rule (D36).
/// </remarks>
internal static class ParquetReportRecordMaterializer
{
    /// <summary>Materializes every row of one row group against <paramref name="schema"/>.</summary>
    /// <param name="rows">The untyped rows (column name to native value); a null cell is an absent key, not a null value — Parquet.Net omits it.</param>
    /// <param name="fileSchema">The Parquet file's own schema, used to resolve declared column names to actual file field names case-insensitively (the stable field list, so a column that is null in a sampled row is still resolvable).</param>
    /// <param name="schema">The report's declared output schema.</param>
    public static IReadOnlyList<ReportRecord> MaterializeRowGroup(
        IList<Dictionary<string, object>> rows, ParquetSchema fileSchema, ReportSchema schema)
    {
        // Resolve each declared column to the file's actual field name once per row group (not per
        // row), so per-row lookup is an exact-key dictionary hit. A declared column absent from the
        // file — or naming a nested/repeated field, see the type-level remarks — resolves to null and
        // yields a null value for every row.
        var fileKey = new string?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
            fileKey[i] = FindFieldName(fileSchema, schema.Columns[i].Name);

        var records = new List<ReportRecord>(rows.Count);
        foreach (Dictionary<string, object> row in rows)
        {
            var values = new object?[schema.Count];
            for (var i = 0; i < schema.Count; i++)
            {
                values[i] = fileKey[i] is { } key && row.TryGetValue(key, out var raw) && raw is not null
                    ? ConvertField(raw, schema.Columns[i].Type)
                    : null;
            }

            records.Add(new ReportRecord(schema, values));
        }

        return records;
    }

    private static string? FindFieldName(ParquetSchema fileSchema, string declaredName)
    {
        foreach (DataField field in fileSchema.DataFields)
        {
            if (string.Equals(field.Name, declaredName, StringComparison.OrdinalIgnoreCase))
                return field.Name;
        }

        return null;
    }

    // A Parquet value already carries a native, correctly-typed CLR value (long, decimal, DateTime,
    // bool, string, byte[], ...) — the file's logical types are explicit in its metadata, so unlike
    // XLSX (where a date is an ambiguously-styled double) there is no type guessing to do. This mostly
    // passes the value through, coercing only when the report's declared ColumnType differs from the
    // native type (e.g. a file's int32 column declared as Integer/long). A value that will not coerce
    // falls back to its native form rather than throwing mid-batch — the same "don't crash on a
    // surprising value" philosophy the CSV/XLSX materializers use.
    private static object? ConvertField(object raw, ColumnType type)
    {
        try
        {
            return type switch
            {
                ColumnType.Integer => raw is long l ? l : Convert.ToInt64(raw, CultureInfo.InvariantCulture),
                ColumnType.Decimal or ColumnType.Money => raw is decimal d ? d : Convert.ToDecimal(raw, CultureInfo.InvariantCulture),
                ColumnType.Boolean => raw is bool b ? b : Convert.ToBoolean(raw, CultureInfo.InvariantCulture),
                // Parquet.Net's untyped path never yields a Guid (no UUID logical type maps to one in
                // this library) — parsing straight from the native value's text form, same as XLSX's
                // own Uuid case, rather than guarding against a shape that can't occur.
                ColumnType.Uuid => Guid.Parse(raw.ToString()!),
                ColumnType.Time => raw switch
                {
                    TimeSpan ts => ts,
                    DateTime tdt => tdt.TimeOfDay,
                    _ => TimeSpan.Parse(raw.ToString()!, CultureInfo.InvariantCulture),
                },
                // Verified empirically against the real Parquet.Net 6.0.3 assembly (not assumed): even
                // a column explicitly written with [ParquetTimestamp(..., isAdjustedToUTC: true)] comes
                // back from DeserializeUntypedAsync as a plain DateTime, never a DateTimeOffset — this
                // library normalizes UTC-adjusted timestamps on read rather than preserving an offset.
                // A code-review pass initially added a DateTimeOffset branch here on the plausible-
                // sounding but unverified assumption that a non-.NET producer's UTC-adjusted timestamp
                // would surface as one; empirical testing disproved it, so no such branch exists.
                ColumnType.Date or ColumnType.DateTime or ColumnType.Timestamp => raw switch
                {
                    DateTime rdt => rdt,
                    _ => DateTime.Parse(raw.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                },
                ColumnType.Binary => raw, // already a byte[] from Parquet
                _ => raw is string ? raw : raw.ToString(), // String, Json
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            return raw;
        }
    }
}
