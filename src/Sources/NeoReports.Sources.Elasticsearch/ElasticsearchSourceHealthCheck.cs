using System.Diagnostics;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// On-demand health check for a registered Elasticsearch/OpenSearch source (ADR D42/D64,
/// <c>type: "elasticsearch"</c>). Probes the configured <c>healthCheckPath</c> (or <c>{url}/{index}</c>
/// itself when unset) with the same configured auth — <c>HEAD</c> first, falling back to <c>GET</c>
/// when the target rejects <c>HEAD</c> (405/501). This only answers "can we reach the index and
/// authenticate" — it does not validate the configured <c>query</c>/<c>sort</c> shape, the same
/// honesty boundary <c>ODataSourceHealthCheck</c>/<c>HttpSourceHealthCheck</c> keep (D36); a source
/// whose author hasn't written a <c>sort</c> yet still passes.
/// </summary>
public sealed class ElasticsearchSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "elasticsearch";

    /// <inheritdoc />
    public async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string url = ElasticsearchConfigProperties.RequireUrl(definition.Properties);
            string index = ElasticsearchConfigProperties.RequireIndex(definition.Properties);
            ElasticsearchSourceOptions options = ElasticsearchConfigProperties.ReadOptions(definition.Properties, requireSort: false);
            HttpClient client = HttpClients.ResolveFrom(services);
            // HealthCheckPath is documented (ElasticsearchSourceOptions.HealthCheckPath/HealthCheckAt)
            // as relative to '{url}/{index}', not the bare base url — built via ElasticsearchUrls.Combine
            // (plain path concatenation) rather than HttpHealthProbe.CombineUrl's relative-Uri
            // resolution, which would silently drop the 'index' segment whenever '{url}/{index}' has
            // no trailing slash (Uri's relative-combination rules replace the last path segment
            // instead of appending after it) — a code-review finding: an earlier version resolved the
            // configured path against the bare base 'url', dropping the index segment the docs promised.
            string targetUrl = ElasticsearchUrls.Combine(url, index, options.HealthCheckPath);

            using HttpResponseMessage response = await HttpHealthProbe.ProbeAsync(client, targetUrl, options.ToAuth(), cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            return response.IsSuccessStatusCode
                ? new SourceHealthResult(Healthy: true, Error: null, stopwatch.Elapsed)
                : new SourceHealthResult(Healthy: false, $"HTTP {(int)response.StatusCode} ({response.StatusCode}).", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SourceHealthResult(Healthy: false, ex.Message, stopwatch.Elapsed);
        }
    }
}
