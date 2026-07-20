using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Airtable;

/// <summary>
/// Config-driven Airtable source for the dynamic path (<c>type: "airtable"</c>, ADR D65). Reads its
/// settings from the source <c>properties</c> (<see cref="AirtableConfigProperties"/>) — required
/// <c>baseId</c>/<c>table</c>, plus field map, auth, etc. Produces positional
/// <see cref="ReportRecord"/>s matched against the report's own declared schema by column name
/// (<see cref="JsonRecordMaterializer"/>).
/// </summary>
public sealed class AirtableConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "airtable";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        string baseId = AirtableConfigProperties.RequireBaseId(source.Properties);
        string table = AirtableConfigProperties.RequireTable(source.Properties);
        AirtableSourceOptions options = AirtableConfigProperties.ReadOptions(source.Properties);
        HttpClient client = HttpClients.ResolveFrom(services);

        ReportRecord Materialize(JsonElement element) => JsonRecordMaterializer.Materialize(element, schema, options.FieldMap);

        return new AirtableBatchSource<ReportRecord>(client, baseId, table, options, schema, Materialize);
    }
}
