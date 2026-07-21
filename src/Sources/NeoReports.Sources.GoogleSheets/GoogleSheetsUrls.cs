using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// Builds the absolute Google Sheets API v4 URLs used by this source (ADR D66). The spreadsheet id
/// is percent-encoded since it is author-configured. The API key is always sent as the well-documented
/// <c>?key=</c> query parameter (not a header) — see D66's authentication section for why.
/// </summary>
internal static class GoogleSheetsUrls
{
    private const string BaseHost = "https://sheets.googleapis.com/v4/spreadsheets";

    private static string SpreadsheetBase(string spreadsheetId) => $"{BaseHost}/{Uri.EscapeDataString(spreadsheetId)}";

    /// <summary>
    /// The <c>values:batchGet</c> endpoint requesting every range in <paramref name="ranges"/> in one
    /// round trip (in order — the response's <c>valueRanges</c> array matches this order), with
    /// <c>valueRenderOption=UNFORMATTED_VALUE</c> (required — see D66; the default render option
    /// returns display-formatted strings, useless for typed conversion) and the configured API key.
    /// Called with just the data-window range once the header has already been cached for this run
    /// (<see cref="GoogleSheetsBatchSource{T}"/>), or with both the header and data-window ranges on
    /// the first page.
    /// </summary>
    public static Uri BatchGet(string spreadsheetId, string apiKey, params string[] ranges)
    {
        var queryParams = new List<(string Key, string Value)>(ranges.Length + 2);
        foreach (string range in ranges)
            queryParams.Add(("ranges", range));

        queryParams.Add(("valueRenderOption", "UNFORMATTED_VALUE"));
        queryParams.Add(("key", apiKey));
        return QueryStrings.AddQuery(SpreadsheetBase(spreadsheetId) + "/values:batchGet", queryParams.ToArray());
    }

    /// <summary>The minimal spreadsheet-metadata endpoint used by the health check.</summary>
    public static Uri Metadata(string spreadsheetId, string apiKey) =>
        QueryStrings.AddQuery(SpreadsheetBase(spreadsheetId), ("fields", "spreadsheetId"), ("key", apiKey));
}
