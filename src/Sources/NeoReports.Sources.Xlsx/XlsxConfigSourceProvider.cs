using Amazon.S3;
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
        Func<CancellationToken, Task<Stream>> openStream = ResolveStreamFactory(source.Properties, services);

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

        if (properties.TryGetValue("hasHeader", out var hasHeaderValue) && TryGetBool(hasHeaderValue, out var hasHeader))
            options.Header(hasHeader);

        if (properties.TryGetValue("sheetName", out var sheetNameValue) && sheetNameValue is string { Length: > 0 } sheetName)
            options.SheetName(sheetName);

        return options;
    }

    private static Func<CancellationToken, Task<Stream>> ResolveStreamFactory(
        IReadOnlyDictionary<string, object?>? properties, IServiceProvider services)
    {
        if (properties is not null
            && properties.TryGetValue("bucket", out var bucketValue)
            && bucketValue is string { Length: > 0 } bucket)
        {
            string key = RequireString(properties, "key");
            // Resolves a DI-registered IAmazonS3 first (custom region/credentials/endpoint, e.g. a
            // LocalStack test double), falling back to ambient AWS credentials only when none is
            // registered — the same precedence NeoReports.Destinations.S3.S3DestinationFactory uses.
            var client = services?.GetService(typeof(IAmazonS3)) as IAmazonS3;
            return ct => S3Stream.OpenAsync(client, bucket, key, ct);
        }

        string path = RequireString(properties, "path");
        return _ => Task.FromResult<Stream>(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true));
    }

    private static string RequireString(IReadOnlyDictionary<string, object?>? properties, string key)
    {
        if (properties is not null && properties.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text))
            return text;

        throw new ConfigurationException($"The XLSX source requires a non-empty '{key}' property (set 'path' for a local file, or 'bucket'+'key' for S3).");
    }

    private static bool TryGetBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case string s when bool.TryParse(s, out var parsed):
                result = parsed;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
