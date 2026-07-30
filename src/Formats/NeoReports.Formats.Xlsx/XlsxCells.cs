using System.Globalization;
using System.Text;
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
            NumericCell(value, reference, numberStyleIndex),
        DateTime dt => DateCell(dt, reference, dateStyleIndex),
        DateTimeOffset dto => DateCell(dto.DateTime, reference, dateStyleIndex),
        DateOnly d => DateCell(d.ToDateTime(TimeOnly.MinValue), reference, dateStyleIndex),
        // Excel has no dedicated time-of-day type separate from a styled number; emit an invariant
        // round-trippable string so the value doesn't shift with the server's culture.
        TimeOnly t => InlineStringCell(t.ToString("O", CultureInfo.InvariantCulture), reference),
        Guid g => InlineStringCell(g.ToString(), reference),
        // Binary would otherwise stringify to "System.Byte[]" (total data loss); Base64 is a faithful,
        // culture-independent text form.
        byte[] bytes => InlineStringCell(Convert.ToBase64String(bytes), reference),
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

    private static Cell NumericCell(object value, string reference, int styleIndex)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        // NaN/Infinity have no valid number-cell representation — Excel rejects a <v> that isn't a
        // finite number and refuses to open the sheet — so emit them as text to keep the file usable.
        return double.IsFinite(number)
            ? NumberCell(number, reference, styleIndex)
            : InlineStringCell(number.ToString(CultureInfo.InvariantCulture), reference);
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
        inlineString.AppendChild(new Text(RemoveIllegalXmlChars(text)) { Space = SpaceProcessingModeValues.Preserve });
        return inlineString;
    }

    // XML 1.0 forbids the C0 control characters other than tab/newline/carriage-return; the OpenXML
    // XmlWriter throws on them, which would abort the whole report over a single stray byte in one
    // cell (e.g. a 0x00/0x01 carried in a text column). Drop them so the file stays well-formed.
    private static string RemoveIllegalXmlChars(string text)
    {
        var firstIllegal = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsIllegalXmlChar(text[i]))
            {
                firstIllegal = i;
                break;
            }
        }

        if (firstIllegal < 0)
            return text;

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, firstIllegal);
        for (var i = firstIllegal; i < text.Length; i++)
        {
            if (!IsIllegalXmlChar(text[i]))
                builder.Append(text[i]);
        }

        return builder.ToString();
    }

    private static bool IsIllegalXmlChar(char c) => c < 0x20 && c is not '\t' and not '\n' and not '\r';
}
