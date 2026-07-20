using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.HubSpot;

/// <summary>
/// On-demand health check for a registered HubSpot source (ADR D42/D65, <c>type: "hubspot"</c>).
/// Probes the configured <c>healthCheckPath</c> (or the resolved object-collection URL itself when
/// unset) with the same configured auth — <c>HEAD</c> first, falling back to <c>GET</c> when the
/// target rejects <c>HEAD</c> (405/501). Reachability/auth only — does not validate the configured
/// <c>properties</c>/field map, the same honesty boundary D61–D64 keep.
/// </summary>
public sealed class HubSpotSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "hubspot";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return HttpHealthProbe.CheckAsync(async () =>
        {
            string objectType = HubSpotConfigProperties.RequireObjectType(definition.Properties);
            HubSpotSourceOptions options = HubSpotConfigProperties.ReadOptions(definition.Properties);
            HttpClient client = HttpClients.ResolveFrom(services);
            // Appended via HubSpotUrls.ObjectCollection's own trailing-segment parameter (plain path
            // concatenation), not HttpHealthProbe.CombineUrl's Uri relative-resolution — the latter
            // would silently replace the collection URL's last segment (the object type) rather than
            // appending after it, since this URL never ends in a trailing slash (code-review finding,
            // the same bug class D64 fixed for Elasticsearch).
            string targetUrl = HubSpotUrls.ObjectCollection(options.BaseUrlValue, objectType, options.HealthCheckPath);

            return await HttpHealthProbe.ProbeAsync(client, targetUrl, options.ToAuth(), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}
