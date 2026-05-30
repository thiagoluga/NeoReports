using NeoReports.Abstractions;

namespace NeoReports.Formats.Csv;

/// <summary>Creates <see cref="CsvWriter"/> instances from captured <see cref="CsvOptions"/>.</summary>
public sealed class CsvWriterFactory : IWriterFactory
{
    private readonly CsvOptions _options;

    /// <summary>Creates the factory with the given options.</summary>
    /// <param name="options">CSV options applied to every created writer.</param>
    public CsvWriterFactory(CsvOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string Format => "csv";

    /// <inheritdoc />
    public IReportWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services) =>
        new CsvWriter(_options);
}
