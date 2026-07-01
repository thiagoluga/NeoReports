using NeoReports.Core.Building;

namespace NeoReports.Xlsx.Pro;

/// <summary>Fluent entry point for the Pro multi-sheet XLSX workbook output.</summary>
public static class Format
{
    /// <summary>
    /// Configures a multi-sheet XLSX workbook output (one worksheet per section). Pass the result to
    /// <c>ReportBuilder.ToSections(...)</c> and declare the sections there.
    /// </summary>
    /// <param name="configure">Optional action to customize header/auto-filter.</param>
    /// <returns>A <see cref="SectionedOutputSpec"/> for <c>ToSections(...)</c>.</returns>
    public static SectionedOutputSpec XlsxWorkbook(Action<XlsxWorkbookOptions>? configure = null)
    {
        var options = new XlsxWorkbookOptions();
        configure?.Invoke(options);
        return new SectionedOutputSpec(new XlsxWorkbookWriterFactory(options));
    }
}
