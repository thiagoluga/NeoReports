using ClosedXML.Excel;
using NeoReports.Abstractions;

namespace NeoReports.Formats.Xlsx;

/// <summary>
/// Writes a single projected value into a ClosedXML cell with the right native Excel type (number,
/// date, boolean, ...) and the column's number/date format. Public so packages built on the XLSX
/// format (e.g. a multi-sheet workbook writer) reuse the exact same cell semantics without
/// duplicating them.
/// </summary>
public static class XlsxCells
{
    /// <summary>Sets a cell's value (native-typed) and applies the column's format.</summary>
    /// <param name="cell">The target ClosedXML cell.</param>
    /// <param name="value">The projected value, or <c>null</c> to clear the cell.</param>
    /// <param name="column">The column metadata (drives the applied format).</param>
    public static void SetCell(IXLCell cell, object? value, ReportColumn column)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(column);

        if (value is null)
        {
            cell.Clear(XLClearOptions.Contents);
            return;
        }

        switch (value)
        {
            case bool b:
                cell.Value = b;
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal:
                cell.Value = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                ApplyFormat(cell, column);
                break;
            case DateTime dt:
                cell.Value = dt;
                ApplyDateFormat(cell, column);
                break;
            case DateTimeOffset dto:
                cell.Value = dto.DateTime;
                ApplyDateFormat(cell, column);
                break;
            case DateOnly d:
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                ApplyDateFormat(cell, column);
                break;
            case Guid g:
                cell.Value = g.ToString();
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }

    private static void ApplyFormat(IXLCell cell, ReportColumn column)
    {
        if (!string.IsNullOrEmpty(column.Format))
            cell.Style.NumberFormat.Format = ExcelFormat.FromNetFormat(column.Format!, column);
    }

    private static void ApplyDateFormat(IXLCell cell, ReportColumn column)
    {
        cell.Style.DateFormat.Format = string.IsNullOrEmpty(column.Format)
            ? "yyyy-mm-dd"
            : ExcelFormat.FromNetDateFormat(column.Format!);
    }
}
