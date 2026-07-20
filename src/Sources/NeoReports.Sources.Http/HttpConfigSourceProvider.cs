using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>
/// Config-driven HTTP/REST source for the dynamic path (<c>type: "http"</c>, ADR D61). Reads its
/// settings from the source <c>properties</c> (<see cref="HttpConfigProperties"/>) — required
/// <c>url</c>, plus pagination strategy, records path, field map, auth, etc. Produces positional
/// <see cref="ReportRecord"/>s matched against the report's own declared schema by column name
/// (<see cref="JsonRecordMaterializer"/>) — the HTTP analog of
/// <c>AdoConfigProperties.MaterializeReportRecord</c>.
/// </summary>
public sealed class HttpConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "http";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        string baseUrl = HttpConfigProperties.RequireUrl(source.Properties);
        HttpSourceOptions options = HttpConfigProperties.ReadOptions(source.Properties);
        HttpClient client = HttpClients.ResolveFrom(services);

        ReportRecord Materialize(JsonElement element) => JsonRecordMaterializer.Materialize(element, schema, options.FieldMap);

        if (options.PaginationStrategy == HttpPaginationStrategy.None)
        {
            var streaming = new HttpStreamingSource<ReportRecord>(client, baseUrl, options, schema, Materialize);
            return new StreamingToBatchSource<ReportRecord>(streaming);
        }

        return new HttpBatchSource<ReportRecord>(client, baseUrl, options, schema, Materialize);
    }
}
