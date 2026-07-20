using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// Config-driven GraphQL source for the dynamic path (<c>type: "graphql"</c>, ADR D63). Reads its
/// settings from the source <c>properties</c> (<see cref="GraphQlConfigProperties"/>) — required
/// <c>url</c>/<c>query</c>/<c>connectionPath</c>, plus node path, paging variable names, static
/// <c>variables</c>, field map, and auth. Produces positional <see cref="ReportRecord"/>s matched
/// against the report's own declared schema by column name (<see cref="JsonRecordMaterializer"/>) —
/// the GraphQL analog of <c>ODataConfigSourceProvider</c>.
/// </summary>
public sealed class GraphQlConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "graphql";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        string endpointUrl = GraphQlConfigProperties.RequireUrl(source.Properties);
        GraphQlSourceOptions options = GraphQlConfigProperties.ReadOptions(source.Properties);
        HttpClient client = HttpClients.ResolveFrom(services);

        ReportRecord Materialize(JsonElement element) => JsonRecordMaterializer.Materialize(element, schema, options.FieldMap);

        return new GraphQlBatchSource<ReportRecord>(client, endpointUrl, options, schema, Materialize);
    }
}
