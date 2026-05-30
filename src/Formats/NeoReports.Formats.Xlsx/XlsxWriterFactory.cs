using NeoReports.Abstractions;

namespace NeoReports.Formats.Xlsx;

/// <summary>Creates <see cref="XlsxWriter"/> instances from captured <see cref="XlsxOptions"/>.</summary>
public sealed class XlsxWriterFactory : IWriterFactory
{
    private readonly XlsxOptions _options;

    /// <summary>Creates the factory with the given options.</summary>
    /// <param name="options">XLSX options applied to every created writer.</param>
    public XlsxWriterFactory(XlsxOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string Format => "xlsx";

    /// <inheritdoc />
    public IReportWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services) =>
        new XlsxWriter(_options);
}
