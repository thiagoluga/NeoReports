using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NeoReports.Formats.Xlsx;

/// <summary>
/// Builds a single streaming XLSX <see cref="Cell"/> with the right native Excel type (number, date,
/// boolean, inline string) and the precomputed per-column style index. Shared by the single-sheet and
/// multi-sheet workbook writers so their cell semantics stay identical. Strings are emitted as INLINE
/// strings (never shared strings), which keeps memory constant — a shared-string table would buffer
/// every distinct value for the life of the write.
/// </summary>
internal static class XlsxCells
{
    /// <summary>
    /// Builds the cell for a projected value, or returns <c>null</c> when the value is <c>null</c> (the
    /// caller omits the cell so it reads back as empty). <paramref name="numberStyleIndex"/> styles
    /// numeric cells and <paramref name="dateStyleIndex"/> styles date cells; both come from
    /// <see cref="XlsxStyleTable"/>.
    /// </summary>
    public static Cell? BuildCell(object? value, string reference, int numberStyleIndex, int dateStyleIndex) => value switch
    {
        null => null,
        bool b => new Cell
        {
            CellReference = reference,
            DataType = CellValues.Boolean,
            CellValue = new CellValue(b ? "1" : "0"),
        },
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            NumberCell(Convert.ToDouble(value, CultureInfo.InvariantCulture), reference, numberStyleIndex),
        DateTime dt => DateCell(dt, reference, dateStyleIndex),
        DateTimeOffset dto => DateCell(dto.DateTime, reference, dateStyleIndex),
        DateOnly d => DateCell(d.ToDateTime(TimeOnly.MinValue), reference, dateStyleIndex),
        Guid g => InlineStringCell(g.ToString(), reference),
        _ => InlineStringCell(value.ToString() ?? string.Empty, reference),
    };

    /// <summary>Builds a bold header cell holding an inline string.</summary>
    public static Cell HeaderCell(string text, string reference) => new()
    {
        CellReference = reference,
        StyleIndex = XlsxStyleTable.HeaderStyleIndex,
        DataType = CellValues.InlineString,
        InlineString = InlineStringElement(text),
    };

    /// <summary>Converts a 0-based column index to Excel column letters (0 → A, 25 → Z, 26 → AA).</summary>
    public static string ColumnLetter(int columnIndex)
    {
        Span<char> buffer = stackalloc char[8];
        var position = buffer.Length;
        var n = columnIndex;
        do
        {
            buffer[--position] = (char)('A' + (n % 26));
            n = (n / 26) - 1;
        }
        while (n >= 0);

        return new string(buffer[position..]);
    }

    private static Cell NumberCell(double value, string reference, int styleIndex)
    {
        var cell = new Cell
        {
            CellReference = reference,
            CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture)),
        };
        if (styleIndex != XlsxStyleTable.DefaultStyleIndex)
            cell.StyleIndex = (uint)styleIndex;
        return cell;
    }

    // Dates are stored as their numeric OADate serial (no data type) styled with a date number-format.
    private static Cell DateCell(DateTime value, string reference, int styleIndex) => new()
    {
        CellReference = reference,
        StyleIndex = (uint)styleIndex,
        CellValue = new CellValue(value.ToOADate().ToString(CultureInfo.InvariantCulture)),
    };

    private static Cell InlineStringCell(string text, string reference) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = InlineStringElement(text),
    };

    private static InlineString InlineStringElement(string text)
    {
        var inlineString = new InlineString();
        inlineString.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return inlineString;
    }
}
