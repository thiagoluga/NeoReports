using NeoReports.Core.Sections;

namespace NeoReports.Xlsx.Pro;

/// <summary>Creates <see cref="XlsxWorkbookWriter"/> instances from captured options.</summary>
public sealed class XlsxWorkbookWriterFactory : ISectionedWriterFactory
{
    private readonly XlsxWorkbookOptions _options;

    /// <summary>Creates the factory with the given options.</summary>
    /// <param name="options">Workbook options applied to every created writer.</param>
    public XlsxWorkbookWriterFactory(XlsxWorkbookOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string Format => "xlsx-workbook";

    /// <inheritdoc />
    public IReportSectionedWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services) =>
        new XlsxWorkbookWriter(_options);
}
