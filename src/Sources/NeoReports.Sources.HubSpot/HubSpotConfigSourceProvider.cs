using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.HubSpot;

/// <summary>
/// Config-driven HubSpot source for the dynamic path (<c>type: "hubspot"</c>, ADR D65). Reads its
/// settings from the source <c>properties</c> (<see cref="HubSpotConfigProperties"/>) — required
/// <c>objectType</c>, plus <c>properties</c>, field map, auth, etc. Produces positional
/// <see cref="ReportRecord"/>s matched against the report's own declared schema by column name
/// (<see cref="JsonRecordMaterializer"/>).
/// </summary>
public sealed class HubSpotConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "hubspot";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        string objectType = HubSpotConfigProperties.RequireObjectType(source.Properties);
        HubSpotSourceOptions options = HubSpotConfigProperties.ReadOptions(source.Properties);
        HttpClient client = HttpClients.ResolveFrom(services);

        ReportRecord Materialize(JsonElement element) => JsonRecordMaterializer.Materialize(element, schema, options.FieldMap);

        return new HubSpotBatchSource<ReportRecord>(client, objectType, options, schema, Materialize);
    }
}
