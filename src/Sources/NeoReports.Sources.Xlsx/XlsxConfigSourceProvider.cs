using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Files.Common;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// Config-driven XLSX source for the dynamic path (<c>type: "xlsx"</c>, ADR D59). Reads its settings
/// from the source <c>properties</c>: either <c>path</c> (local file) or <c>bucket</c>+<c>key</c>
/// (S3), plus optional <c>sheetName</c> (default: the workbook's first sheet) and <c>hasHeader</c>
/// (default <c>true</c>). Produces positional <see cref="ReportRecord"/>s matched against the report's
/// own declared schema by column name (falling back to positional alignment when the sheet has no
/// header) — the XLSX analog of <c>AdoConfigProperties.MaterializeReportRecord</c>.
/// </summary>
public sealed class XlsxConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "xlsx";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        XlsxReaderOptions options = ReadOptions(source.Properties);
        Func<CancellationToken, Task<Stream>> openStream =
            FileSourceProperties.ResolveStreamFactory("XLSX", source.Properties, services);

        var streaming = new XlsxStreamingSource<ReportRecord>(
            openStream, options, schema,
            (index, row) => XlsxReportRecordMaterializer.Materialize(index, row, schema));

        return new StreamingToBatchSource<ReportRecord>(streaming);
    }

    private static XlsxReaderOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties)
    {
        var options = new XlsxReaderOptions();
        if (properties is null)
            return options;

        if (properties.TryGetValue("hasHeader", out var hasHeaderValue) && FileSourceProperties.TryGetBool(hasHeaderValue, out var hasHeader))
            options.Header(hasHeader);

        if (properties.TryGetValue("sheetName", out var sheetNameValue) && sheetNameValue is string { Length: > 0 } sheetName)
            options.SheetName(sheetName);

        return options;
    }
}
