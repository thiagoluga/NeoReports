using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>
/// Config-driven Salesforce source for the dynamic path (<c>type: "salesforce"</c>, ADR D67). Reads
/// its settings from the source <c>properties</c> (<see cref="SalesforceConfigProperties"/>) —
/// required <c>instanceUrl</c>/<c>soql</c>/<c>bearerToken</c>, plus optional <c>apiVersion</c>/
/// <c>fieldMap</c>/<c>headers</c>/<c>healthCheckPath</c>. Produces positional <see cref="ReportRecord"/>s
/// matched against the report's own declared schema by column name — each record's fields are
/// matched directly, with no envelope to descend into (<see cref="JsonRecordMaterializer"/>).
/// </summary>
public sealed class SalesforceConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "salesforce";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        string instanceUrl = SalesforceConfigProperties.RequireInstanceUrl(source.Properties);
        string soql = SalesforceConfigProperties.RequireSoql(source.Properties);
        SalesforceSourceOptions options = SalesforceConfigProperties.ReadOptions(source.Properties);
        HttpClient client = HttpClients.ResolveFrom(services);

        ReportRecord Materialize(JsonElement element) => JsonRecordMaterializer.Materialize(element, schema, options.FieldMap);

        return new SalesforceBatchSource<ReportRecord>(client, instanceUrl, soql, options, schema, Materialize);
    }
}
