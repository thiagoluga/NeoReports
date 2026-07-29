using NeoReports.Core.Sections;
using NeoReports.Licensing;

namespace NeoReports.Xlsx.Pro;

/// <summary>Creates <see cref="XlsxWorkbookWriter"/> instances from captured options.</summary>
public sealed class XlsxWorkbookWriterFactory : ISectionedWriterFactory
{
    private readonly XlsxWorkbookOptions _options;

    /// <summary>
    /// Creates the factory with the given options. Requires a valid NeoReports Pro license
    /// (ADR D70) — gating the factory's constructor covers every route into this writer at once:
    /// <see cref="Format.XlsxWorkbook"/>, <c>AddXlsxWorkbook</c>, and direct construction.
    /// </summary>
    /// <param name="options">Workbook options applied to every created writer.</param>
    /// <exception cref="NeoReportsLicenseException">No valid NeoReports Pro license is configured.</exception>
    public XlsxWorkbookWriterFactory(XlsxWorkbookOptions options)
    {
        ProLicenseGate.EnsureValidated();
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Format => "xlsx-workbook";

    /// <inheritdoc />
    public IReportSectionedWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services) =>
        new XlsxWorkbookWriter(_options);
}
