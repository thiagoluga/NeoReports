using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// Config-driven Elasticsearch/OpenSearch source for the dynamic path (<c>type: "elasticsearch"</c>,
/// ADR D64). Reads its settings from the source <c>properties</c>
/// (<see cref="ElasticsearchConfigProperties"/>) — required <c>url</c>/<c>index</c>/<c>sort</c>,
/// plus an optional static <c>query</c>, field map, auth, etc. Produces positional
/// <see cref="ReportRecord"/>s matched against the report's own declared schema by column name
/// (<see cref="JsonRecordMaterializer"/>) — the Elasticsearch analog of
/// <c>AdoConfigProperties.MaterializeReportRecord</c>.
/// </summary>
public sealed class ElasticsearchConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "elasticsearch";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        string url = ElasticsearchConfigProperties.RequireUrl(source.Properties);
        string index = ElasticsearchConfigProperties.RequireIndex(source.Properties);
        ElasticsearchSourceOptions options = ElasticsearchConfigProperties.ReadOptions(source.Properties);
        HttpClient client = HttpClients.ResolveFrom(services);

        ReportRecord Materialize(JsonElement element) => JsonRecordMaterializer.Materialize(element, schema, options.FieldMap);

        return new ElasticsearchBatchSource<ReportRecord>(client, url, index, options, schema, Materialize);
    }
}
