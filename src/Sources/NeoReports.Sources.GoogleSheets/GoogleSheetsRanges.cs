namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// Builds A1-notation range strings for the Google Sheets API (ADR D66). The sheet name is always
/// single-quoted (valid even when it doesn't strictly need quoting, e.g. no spaces) with an embedded
/// quote doubled — the same escaping A1 notation itself requires for a sheet name containing a
/// literal <c>'</c>.
/// </summary>
internal static class GoogleSheetsRanges
{
    /// <summary>The header row's range, e.g. <c>'Sheet1'!A1:F1</c>.</summary>
    public static string Header(string sheet, int headerRow, string firstColumn, string lastColumn) =>
        $"{QuoteSheet(sheet)}!{firstColumn}{headerRow}:{lastColumn}{headerRow}";

    /// <summary>
    /// One fixed-size data-row window, e.g. <c>'Sheet1'!A2:F101</c> for <paramref name="firstDataRow"/> 2,
    /// <paramref name="offset"/> 0, <paramref name="pageSize"/> 100.
    /// </summary>
    public static string DataWindow(string sheet, int firstDataRow, int offset, int pageSize, string firstColumn, string lastColumn)
    {
        int startRow = firstDataRow + offset;
        int endRow = startRow + pageSize - 1;
        return $"{QuoteSheet(sheet)}!{firstColumn}{startRow}:{lastColumn}{endRow}";
    }

    private static string QuoteSheet(string sheet) => $"'{sheet.Replace("'", "''", StringComparison.Ordinal)}'";
}
