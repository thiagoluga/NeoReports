using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// Streaming XLSX worksheet row reader — the read-direction counterpart of
/// <c>NeoReports.Formats.Xlsx.XlsxWriter</c>. Uses <see cref="OpenXmlReader"/> (the SAX-style,
/// forward-only reader Microsoft recommends for very large workbooks) to walk one row at a time,
/// so only a single row's cells are held in memory regardless of sheet size (rule 8) — unlike
/// ClosedXML, which materializes the whole workbook DOM and was ruled out for reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synchronous by design.</b> The method returns <see cref="IEnumerable{T}"/>, not
/// <c>IAsyncEnumerable</c> — deliberately, unlike the CSV reader. <see cref="OpenXmlReader.Read"/>
/// sits on top of an <c>XmlReader</c> over a <c>ZipArchiveEntry</c> stream, and neither the OpenXML
/// SDK nor that underlying <c>XmlReader</c> exposes a genuine async read path; an <c>async</c>
/// signature here would only wrap synchronous CPU/zip-inflate work and lie about the I/O model. The
/// caller (a teammate's streaming source) can hop this onto a background thread if it must not block.
/// </para>
/// <para>
/// <b>Typed values.</b> Each yielded <c>object?[]</c> holds native CLR values — <see cref="double"/>
/// for numbers, <see cref="string"/> for text and error literals, <see cref="bool"/> for booleans,
/// <see cref="DateTime"/> for date-styled numeric cells, and <c>null</c> for empty cells.
/// </para>
/// <para>
/// <b>Column alignment.</b> Excel omits empty cells from the XML, so cells are placed by the column
/// letters parsed from each cell's <c>CellReference</c> (A→0, B→1, …, AA→26), never by their order in
/// the row. A row is sized to its own highest referenced column, with <c>null</c> for any gaps;
/// aligning a short row against a wider header is left to the materializer layer above.
/// </para>
/// </remarks>
internal static class XlsxRowReader
{
    /// <summary>
    /// Reads a worksheet of <paramref name="stream"/> (an open XLSX package — local file or S3, the
    /// caller resolves the source), yielding one <c>object?[]</c> per row including the header row (the
    /// caller decides whether the first yielded row is a header).
    /// </summary>
    /// <param name="stream">The XLSX package to read. Not disposed by this method — the caller owns it.</param>
    /// <param name="sheetName">
    /// The worksheet to read; when <c>null</c> or empty, the workbook's first sheet is used.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read, checked once per row.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the workbook has no worksheets, or no sheet matches <paramref name="sheetName"/>.
    /// </exception>
    public static IEnumerable<object?[]> ReadRows(
        Stream stream, string? sheetName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidOperationException("The XLSX package has no workbook part.");

        var worksheetPart = ResolveWorksheetPart(workbookPart, sheetName);
        var sharedStrings = SharedStringCache.Load(workbookPart);
        var numberFormats = NumberFormatCache.Build(workbookPart);

        // Reused across rows (via Clear() below) rather than reallocated per row — the array it feeds
        // into is a fresh copy each time, so the scratch list itself is safe to recycle (mirrors
        // CsvRowReader's identical reuse of its own per-row scratch list).
        var cells = new List<(int Column, object? Value)>();

        using var reader = OpenXmlReader.Create(worksheetPart);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row))
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            cells.Clear();
            yield return ReadRow(reader, cells, sharedStrings, numberFormats);
        }
    }

    /// <summary>Reads the cells of the row the <paramref name="reader"/> is currently positioned on.</summary>
    private static object?[] ReadRow(
        OpenXmlReader reader, List<(int Column, object? Value)> cells, string[] sharedStrings, NumberFormatCache numberFormats)
    {
        // Accumulate (columnIndex, value) then size the array to the highest column — cells arrive in
        // column order, but relying on that would misalign a row whose gaps Excel simply omitted.
        var maxColumn = -1;

        if (reader.ReadFirstChild())
        {
            do
            {
                if (reader.ElementType != typeof(Cell))
                    continue;

                var cell = (Cell)reader.LoadCurrentElement()!;
                var column = ColumnIndex(cell.CellReference?.Value);
                if (column < 0)
                    continue; // a cell with no reference cannot be positioned; skip rather than misalign

                var value = ReadCellValue(cell, sharedStrings, numberFormats);
                cells.Add((column, value));
                if (column > maxColumn)
                    maxColumn = column;
            }
            while (reader.ReadNextSibling());
        }

        if (maxColumn < 0)
            return Array.Empty<object?>();

        var row = new object?[maxColumn + 1];
        foreach (var (column, value) in cells)
            row[column] = value;

        return row;
    }

    /// <summary>Resolves a single cell to its native CLR value, or <c>null</c> when the cell is empty.</summary>
    /// <remarks>
    /// <see cref="CellValues"/> is a struct (not a compile-time enum) in OpenXML 3.x, so the type is
    /// matched with equality checks rather than a <c>switch</c> on constant labels.
    /// </remarks>
    private static object? ReadCellValue(Cell cell, string[] sharedStrings, NumberFormatCache numberFormats)
    {
        var dataType = cell.DataType?.Value;

        // Inline strings live in a child <is> element, not <v>, so they are handled before the empty check.
        if (dataType == CellValues.InlineString)
            return cell.InlineString?.Text?.Text ?? string.Empty;

        var raw = cell.CellValue?.InnerText;
        if (string.IsNullOrEmpty(raw))
            return null;

        if (dataType == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                && idx >= 0 && idx < sharedStrings.Length
                    ? sharedStrings[idx]
                    : string.Empty;
        }

        if (dataType == CellValues.String) // a formula's cached string result
            return raw;

        if (dataType == CellValues.Boolean)
            return raw == "1";

        if (dataType == CellValues.Error)
            return raw; // e.g. "#DIV/0!" — surface the literal, do not throw

        if (dataType == CellValues.Date)
        {
            // ISO-8601 date stored as text (rare; most dates are numeric serials, see below).
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoDate)
                ? isoDate
                : raw;
        }

        // No data type (Excel's default) or an explicit number: it is numeric, but a date-styled numeric
        // cell is an OA date serial (days since 1899-12-30) that must become a DateTime.
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return raw;

        return numberFormats.IsDateFormat(cell.StyleIndex?.Value)
            ? DateTime.FromOADate(number)
            : number;
    }

    /// <summary>
    /// Converts the column part of a cell reference (e.g. <c>"C5"</c> → <c>2</c>, <c>"AA10"</c> →
    /// <c>26</c>) to a 0-based column index, treating the letters as a base-26 (A–Z) number. Returns
    /// <c>-1</c> when the reference is null/empty or has no leading letters.
    /// </summary>
    internal static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
            return -1;

        var index = 0;
        var sawLetter = false;
        foreach (var c in cellReference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                index = (index * 26) + (c - 'A' + 1);
                sawLetter = true;
            }
            else if (c is >= 'a' and <= 'z')
            {
                index = (index * 26) + (c - 'a' + 1);
                sawLetter = true;
            }
            else
            {
                break; // the first digit ends the column part
            }
        }

        return sawLetter ? index - 1 : -1; // -1 shifts the 1-based base-26 value to a 0-based index
    }

    /// <summary>Finds the worksheet part by sheet name, or the first sheet when no name is given.</summary>
    private static WorksheetPart ResolveWorksheetPart(WorkbookPart workbookPart, string? sheetName)
    {
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray();
        if (sheets is null || sheets.Length == 0)
            throw new InvalidOperationException("The XLSX workbook contains no worksheets.");

        Sheet sheet = string.IsNullOrEmpty(sheetName)
            ? sheets[0]
            : Array.Find(sheets, s => string.Equals(s.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The XLSX workbook has no worksheet named '{sheetName}'.");

        var relationshipId = sheet.Id?.Value
            ?? throw new InvalidOperationException("The target worksheet has no relationship id.");

        return (WorksheetPart)workbookPart.GetPartById(relationshipId);
    }
}
