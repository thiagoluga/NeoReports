using System.Text.Json.Serialization;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// The pagination state Google Sheets' synthesized row-window paging needs to fetch its next page
/// (ADR D66): the row offset the next window starts at, relative to the first data row. There is no
/// server-provided cursor of any kind — the Sheets API has no pagination mechanism at all.
/// </summary>
/// <param name="Offset">Rows already consumed; the next window starts at <c>firstDataRow + Offset</c>.</param>
internal sealed record GoogleSheetsCursorState([property: JsonPropertyName("o")] int Offset = 0);

/// <summary>
/// Encodes/decodes <see cref="GoogleSheetsCursorState"/> into the single opaque <c>string?</c> cursor
/// the pipeline carries (D3 rule) — a thin wrapper over the shared <see cref="OpaqueCursor"/>
/// Base64(UTF-8(JSON)) mechanism.
/// </summary>
internal static class GoogleSheetsPagination
{
    private static readonly GoogleSheetsCursorState FirstPage = new();

    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode(GoogleSheetsCursorState state) => OpaqueCursor.Encode(state);

    /// <summary>Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to empty state.</summary>
    public static GoogleSheetsCursorState Decode(string? cursor) => OpaqueCursor.Decode<GoogleSheetsCursorState>(cursor) ?? FirstPage;
}
