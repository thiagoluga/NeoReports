using DocumentFormat.OpenXml.Spreadsheet;
using NeoReports.Abstractions;

namespace NeoReports.Formats.Xlsx;

/// <summary>
/// Precomputes the distinct cell styles a streaming XLSX writer needs, so the workbook styles part
/// can be written in full BEFORE any sheet row references a style index. OpenXML requires the styles
/// part to come first; number/date formats are per-column, so this
/// table registers each column's numeric and date style once (deduping identical Excel format codes)
/// and hands back the style indices to stamp on cells while streaming.
/// </summary>
/// <remarks>
/// Style layout: index 0 is the default (no format), index 1 is the bold header, and each distinct
/// custom format code adds one <c>numFmt</c> (ids from 164) plus one <c>cellXf</c> (indices from 2).
/// Two columns with the same format code share a single entry.
/// </remarks>
internal sealed class XlsxStyleTable
{
    /// <summary>Style index of a cell with no explicit format (numbers without a format, strings, booleans).</summary>
    public const int DefaultStyleIndex = 0;

    /// <summary>Style index of the bold header cells.</summary>
    public const int HeaderStyleIndex = 1;

    // Custom number-format ids must start at 164 (0-163 are reserved built-ins).
    private const uint FirstCustomNumFmtId = 164;

    private readonly Dictionary<string, int> _styleIndexByCode = new(StringComparer.Ordinal);
    private readonly List<string> _formatCodes = [];

    /// <summary>
    /// Registers the numeric style for a column and returns the style index for its number cells.
    /// A column without a format uses the default style.
    /// </summary>
    public int RegisterNumberStyle(ReportColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return string.IsNullOrEmpty(column.Format)
            ? DefaultStyleIndex
            : GetOrAdd(ExcelFormat.FromNetFormat(column.Format!, column));
    }

    /// <summary>
    /// Registers the date style for a column and returns the style index for its date cells. Dates
    /// always carry a format (the column's translated format, or <c>yyyy-mm-dd</c> when absent).
    /// </summary>
    public int RegisterDateStyle(ReportColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var code = string.IsNullOrEmpty(column.Format)
            ? "yyyy-mm-dd"
            : ExcelFormat.FromNetDateFormat(column.Format!);
        return GetOrAdd(code);
    }

    /// <summary>Builds the fully-populated stylesheet to write into the workbook styles part.</summary>
    public Stylesheet Build()
    {
        var stylesheet = new Stylesheet();

        if (_formatCodes.Count > 0)
        {
            var numberingFormats = new NumberingFormats { Count = (uint)_formatCodes.Count };
            for (var i = 0; i < _formatCodes.Count; i++)
            {
                numberingFormats.Append(new NumberingFormat
                {
                    NumberFormatId = FirstCustomNumFmtId + (uint)i,
                    FormatCode = _formatCodes[i],
                });
            }

            stylesheet.Append(numberingFormats);
        }

        stylesheet.Append(new Fonts(new Font(), new Font(new Bold())) { Count = 2 });
        stylesheet.Append(new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        { Count = 2 });
        stylesheet.Append(new Borders(new Border()) { Count = 1 });
        stylesheet.Append(new CellStyleFormats(
            new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 })
        { Count = 1 });

        var cellFormats = new CellFormats { Count = (uint)(2 + _formatCodes.Count) };
        // Index 0: default. Index 1: bold header.
        cellFormats.Append(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 });
        cellFormats.Append(new CellFormat
        {
            NumberFormatId = 0,
            FontId = 1,
            FillId = 0,
            BorderId = 0,
            FormatId = 0,
            ApplyFont = true,
        });
        for (var i = 0; i < _formatCodes.Count; i++)
        {
            cellFormats.Append(new CellFormat
            {
                NumberFormatId = FirstCustomNumFmtId + (uint)i,
                FontId = 0,
                FillId = 0,
                BorderId = 0,
                FormatId = 0,
                ApplyNumberFormat = true,
            });
        }

        stylesheet.Append(cellFormats);
        stylesheet.Append(new CellStyles(
            new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 })
        { Count = 1 });

        return stylesheet;
    }

    private int GetOrAdd(string formatCode)
    {
        if (_styleIndexByCode.TryGetValue(formatCode, out var existing))
            return existing;

        var index = 2 + _formatCodes.Count; // 0 = default, 1 = header, custom formats follow
        _formatCodes.Add(formatCode);
        _styleIndexByCode[formatCode] = index;
        return index;
    }
}
