using System.Globalization;
using System.Text.Json;
using NeoReports.Core.Sources;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// Maps a Google Sheets row's cells to a <typeparamref name="T"/> instance by header name (ADR D66)
/// — reuses <see cref="ReflectedRowShape{T}.Materialize"/>, the same reflection/materialization loop
/// the CSV/XLSX/ADO families share (ADR D58/D59); only the "read and convert one cell" step differs.
/// </summary>
/// <typeparam name="T">The row type to materialize.</typeparam>
internal sealed class GoogleSheetsRecordMaterializer<T>
{
    private readonly ReflectedRowShape<T> _shape = new();

    /// <summary>Materializes one row given the header's column-name-to-ordinal map.</summary>
    /// <param name="ordinalByName">Header column ordinal by name (case-insensitive).</param>
    /// <param name="row">The row's raw cell values, aligned to the header.</param>
    public T Materialize(IReadOnlyDictionary<string, int> ordinalByName, JsonElement[] row) =>
        _shape.Materialize(ordinalByName, row.Length, (ordinal, targetType) => ConvertValue(row[ordinal], targetType));

    private static object? ConvertValue(JsonElement cell, Type targetType)
    {
        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(string))
            return GoogleSheetsCellText.TextOrNull(cell);

        if (underlying == typeof(DateTime))
            return cell.ValueKind == JsonValueKind.Number ? GoogleSheetsCellText.Epoch.AddDays(cell.GetDouble()) : ReflectedRowShape<T>.DefaultFor(targetType);

        if (underlying == typeof(TimeSpan))
            return cell.ValueKind == JsonValueKind.Number ? TimeSpan.FromDays(cell.GetDouble()) : ReflectedRowShape<T>.DefaultFor(targetType);

        string raw = GoogleSheetsCellText.Text(cell);

        // An empty/blank cell (a mid-row gap the API still represents, per D66) is a genuine "no
        // value" for a non-string target — falls back to the type's default rather than throwing,
        // mirroring CsvRecordMaterializer's identical empty-field convention.
        if (cell.ValueKind != JsonValueKind.Number && string.IsNullOrEmpty(raw))
            return ReflectedRowShape<T>.DefaultFor(targetType);

        try
        {
            if (underlying == typeof(bool))
                return cell.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => bool.Parse(raw) };
            if (underlying == typeof(Guid))
                return Guid.Parse(raw);

            return cell.ValueKind == JsonValueKind.Number
                ? Convert.ChangeType(cell.GetDouble(), underlying, CultureInfo.InvariantCulture)
                : Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            return ReflectedRowShape<T>.DefaultFor(targetType);
        }
    }
}
