using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.OData;

/// <summary>
/// Config-driven OData source for the dynamic path (<c>type: "odata"</c>, ADR D62). Reads its
/// settings from the source <c>properties</c> (<see cref="ODataConfigProperties"/>) — required
/// <c>url</c>, plus pagination strategy, records path, static <c>$filter</c>/<c>$select</c>/
/// <c>$orderby</c>/<c>$top</c>, field map, auth, etc. Produces positional
/// <see cref="ReportRecord"/>s matched against the report's own declared schema by column name
/// (<see cref="JsonRecordMaterializer"/>) — the OData analog of
/// <c>AdoConfigProperties.MaterializeReportRecord</c>. Unlike the HTTP family (P4a), there is no
/// <c>None</c>-equivalent single-response streaming case — every response is a bounded OData page.
/// </summary>
public sealed class ODataConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "odata";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        string resourceUrl = ODataConfigProperties.RequireUrl(source.Properties);
        ODataSourceOptions options = ODataConfigProperties.ReadOptions(source.Properties);
        HttpClient client = HttpClients.ResolveFrom(services);

        ReportRecord Materialize(JsonElement element) => JsonRecordMaterializer.Materialize(element, schema, options.FieldMap);

        return new ODataBatchSource<ReportRecord>(client, resourceUrl, options, schema, Materialize);
    }
}
