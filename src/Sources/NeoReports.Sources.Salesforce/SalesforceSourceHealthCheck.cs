using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>
/// On-demand health check for a registered Salesforce source (ADR D42/D67, <c>type: "salesforce"</c>).
/// Probes the REST API's "list available resources" endpoint (<c>GET /services/data/{apiVersion}/</c>)
/// with the configured auth — reachability/auth only, deliberately independent of the configured
/// <c>soql</c>, the same honesty boundary D63's GraphQL fix established.
/// </summary>
public sealed class SalesforceSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "salesforce";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return HttpHealthProbe.CheckAsync(async () =>
        {
            string instanceUrl = SalesforceConfigProperties.RequireInstanceUrl(definition.Properties);
            SalesforceSourceOptions options = SalesforceConfigProperties.ReadOptions(definition.Properties);
            HttpClient client = HttpClients.ResolveFrom(services);

            // Appended via SalesforceUrls.Resources' own trailing-segment parameter (plain path
            // concatenation), not HttpHealthProbe.CombineUrl's Uri relative-resolution — a configured
            // healthCheckPath starting with '/' would otherwise be treated as an absolute-path
            // reference and replace this URL's entire path, not just append after it (code-review
            // finding, the same D64/D65 bug class).
            Uri targetUrl = SalesforceUrls.Resources(instanceUrl, options.ApiVersionValue, options.HealthCheckPath);
            return await HttpHealthProbe.ProbeAsync(client, targetUrl.ToString(), options.ToAuth(), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}
