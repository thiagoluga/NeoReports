using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Files.Common;

namespace NeoReports.Sources.Csv;

/// <summary>
/// Config-driven CSV source for the dynamic path (<c>type: "csv"</c>, ADR D58). Reads its settings
/// from the source <c>properties</c>: either <c>path</c> (local file) or <c>bucket</c>+<c>key</c>
/// (S3), plus optional <c>hasHeader</c> (default <c>true</c>) and <c>delimiter</c> (default
/// <c>,</c>). Produces positional <see cref="ReportRecord"/>s matched against the report's own
/// declared schema by column name (falling back to positional alignment when the file has no
/// header) — the CSV analog of <c>AdoConfigProperties.MaterializeReportRecord</c>.
/// </summary>
public sealed class CsvConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "csv";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        CsvReaderOptions options = ReadOptions(source.Properties);
        Func<CancellationToken, Task<Stream>> openStream =
            FileSourceProperties.ResolveStreamFactory("CSV", source.Properties, services);

        var streaming = new CsvStreamingSource<ReportRecord>(
            openStream, options, schema,
            (index, row) => CsvReportRecordMaterializer.Materialize(index, row, schema));

        return new StreamingToBatchSource<ReportRecord>(streaming);
    }

    private static CsvReaderOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties)
    {
        var options = new CsvReaderOptions();
        if (properties is null)
            return options;

        if (properties.TryGetValue("hasHeader", out var hasHeaderValue) && FileSourceProperties.TryGetBool(hasHeaderValue, out var hasHeader))
            options.Header(hasHeader);

        if (properties.TryGetValue("delimiter", out var delimiterValue) && delimiterValue is string { Length: 1 } delimiterText)
            options.Delimiter(delimiterText[0]);

        return options;
    }
}
