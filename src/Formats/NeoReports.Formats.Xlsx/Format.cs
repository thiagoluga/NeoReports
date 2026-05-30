using NeoReports.Core.Building;

namespace NeoReports.Formats.Xlsx;

/// <summary>Fluent entry point for the XLSX output format.</summary>
public static class Format
{
    /// <summary>Configures an XLSX output.</summary>
    /// <param name="configure">Optional action to set sheet name, header, and auto-filter.</param>
    /// <returns>An <see cref="OutputSpec"/> to pass to <c>ReportBuilder.To(...)</c>.</returns>
    public static OutputSpec Xlsx(Action<XlsxOptions>? configure = null)
    {
        var options = new XlsxOptions();
        configure?.Invoke(options);
        return new OutputSpec(new XlsxWriterFactory(options));
    }
}
