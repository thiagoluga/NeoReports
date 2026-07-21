using System.Text.Json;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// Shared cell-to-text/date decoding for both the typed (<see cref="GoogleSheetsRecordMaterializer{T}"/>)
/// and dynamic (<see cref="GoogleSheetsReportRecordMaterializer"/>) materializers (ADR D66) — every
/// request sets <c>valueRenderOption=UNFORMATTED_VALUE</c>, so a cell arrives as a JSON string, a
/// JSON number (dates/times included — Sheets' serial-number-since-1899-12-30 convention, not an
/// ISO-8601 string), or a JSON bool, never pre-formatted display text. Promoted here once this exact
/// decode logic was found duplicated three times across the two materializers (code review finding).
/// </summary>
internal static class GoogleSheetsCellText
{
    /// <summary>Sheets' date/time serial-number epoch — day 0.</summary>
    public static readonly DateTime Epoch = new(1899, 12, 30);

    /// <summary>
    /// The cell's text form, or <c>null</c> for a JSON kind with no sensible text representation —
    /// used by a string-typed target, which should stay <c>null</c> rather than become <c>""</c> for
    /// e.g. a JSON <c>null</c> cell.
    /// </summary>
    public static string? TextOrNull(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.String => cell.GetString(),
        JsonValueKind.Number => cell.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>The cell's text form, or <c>""</c> for a JSON kind with no sensible text representation.</summary>
    public static string Text(JsonElement cell) => TextOrNull(cell) ?? string.Empty;
}
