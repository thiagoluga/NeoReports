using NeoReports.Core.Building;

namespace NeoReports.Formats.Csv;

/// <summary>Fluent entry points for output formats. CSV lives here; XLSX adds its own overload.</summary>
public static class Format
{
    /// <summary>Configures a CSV output.</summary>
    /// <param name="configure">Optional action to customize delimiter, encoding, and header.</param>
    /// <returns>An <see cref="OutputSpec"/> to pass to <c>ReportBuilder.To(...)</c>.</returns>
    public static OutputSpec Csv(Action<CsvOptions>? configure = null)
    {
        var options = new CsvOptions();
        configure?.Invoke(options);
        return new OutputSpec(new CsvWriterFactory(options));
    }
}
