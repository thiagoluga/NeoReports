using NeoReports.Abstractions;

namespace NeoReports.Formats.Xlsx;

/// <summary>
/// Best-effort translation of .NET format strings to Excel number/date format codes. The mapping
/// is intentionally small: it covers the common standard specifiers used by the v1 reports and
/// passes through anything that already looks like an Excel custom format.
/// </summary>
internal static class ExcelFormat
{
    public static string FromNetFormat(string netFormat, ReportColumn column)
    {
        // Standard numeric format specifiers like "C2", "N0", "P1", "F2".
        if (netFormat.Length is >= 1 and <= 3)
        {
            var specifier = char.ToUpperInvariant(netFormat[0]);
            var digits = netFormat.Length > 1 && int.TryParse(netFormat[1..], out var d) ? d : (int?)null;

            switch (specifier)
            {
                case 'C':
                    return "#,##0" + Decimals(digits ?? 2);
                case 'N':
                    return "#,##0" + Decimals(digits ?? 2);
                case 'F':
                    return "0" + Decimals(digits ?? 2);
                case 'P':
                    return "0" + Decimals(digits ?? 2) + "%";
                case 'D':
                    return "0";
            }
        }

        // Already an Excel-style custom format (or close enough): pass through.
        return netFormat;
    }

    public static string FromNetDateFormat(string netFormat) =>
        netFormat
            .Replace("yyyy", "yyyy")
            .Replace("MM", "mm")
            .Replace("dd", "dd")
            .Replace("HH", "hh")
            .Replace("ss", "ss");

    private static string Decimals(int count) => count <= 0 ? string.Empty : "." + new string('0', count);
}
