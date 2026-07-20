using System.Diagnostics;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Airtable;

/// <summary>
/// On-demand health check for a registered Airtable source (ADR D42/D65, <c>type: "airtable"</c>).
/// Probes the configured <c>healthCheckPath</c> (or the resolved table URL itself when unset) with
/// the same configured auth — <c>HEAD</c> first, falling back to <c>GET</c> when the target rejects
/// <c>HEAD</c> (405/501). Reachability/auth only — does not validate the configured field map, the
/// same honesty boundary D61–D65 keep.
/// </summary>
public sealed class AirtableSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "airtable";

    /// <inheritdoc />
    public async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string baseId = AirtableConfigProperties.RequireBaseId(definition.Properties);
            string table = AirtableConfigProperties.RequireTable(definition.Properties);
            AirtableSourceOptions options = AirtableConfigProperties.ReadOptions(definition.Properties);
            HttpClient client = HttpClients.ResolveFrom(services);
            // Appended via AirtableUrls.Table's own trailing-segment parameter (plain path
            // concatenation), not HttpHealthProbe.CombineUrl's Uri relative-resolution — the latter
            // would silently replace the table URL's last segment (the table name) rather than
            // appending after it, since this URL never ends in a trailing slash (code-review finding,
            // the same bug class D64 fixed for Elasticsearch).
            string targetUrl = AirtableUrls.Table(options.BaseUrlValue, baseId, table, options.HealthCheckPath);

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
