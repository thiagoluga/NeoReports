using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// Loads an XLSX workbook's shared-string table into a flat, index-addressable <c>string[]</c> once,
/// so a cell referencing string index <c>n</c> is an O(1) array lookup during the row scan.
/// </summary>
/// <remarks>
/// XLSX de-duplicates repeated cell text into a single <c>&lt;sst&gt;</c> table and stores only the
/// integer index on each cell. Enumerating <c>SharedStringTable.Elements&lt;SharedStringItem&gt;()</c>
/// with <c>.ElementAt(index)</c> per cell is an O(n²) trap — <c>IEnumerable</c> re-walks from the
/// start every call — so we materialize the table exactly once. The array's size is O(distinct
/// strings), which is bounded by the workbook's vocabulary, not its row count, so it does not violate
/// the constant-memory-per-row rule (rule 8): a million rows that repeat the same handful of strings
/// still cost a handful of entries here.
/// </remarks>
internal static class SharedStringCache
{
    /// <summary>An empty table, shared for workbooks that carry no shared strings at all.</summary>
    private static readonly string[] Empty = Array.Empty<string>();

    /// <summary>
    /// Builds the indexed shared-string array for <paramref name="workbookPart"/>. Returns an empty
    /// array when the workbook has no <see cref="SharedStringTablePart"/>.
    /// </summary>
    /// <param name="workbookPart">The workbook part to read the shared-string table from.</param>
    public static string[] Load(WorkbookPart workbookPart)
    {
        ArgumentNullException.ThrowIfNull(workbookPart);

        var table = workbookPart.SharedStringTablePart?.SharedStringTable;
        if (table is null)
            return Empty;

        // InnerText concatenates the text of every run inside the item, which is exactly the value a
        // reader wants for a rich-text ("runs") shared string — the formatting is irrelevant to data.
        return table.Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToArray();
    }
}
