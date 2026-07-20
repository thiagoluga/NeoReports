using System.Diagnostics;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.OData;

/// <summary>
/// On-demand health check for a registered OData source (ADR D42/D62, <c>type: "odata"</c>). Probes
/// the configured <c>healthCheckPath</c> (or the resource URL itself when unset) with the same
/// configured auth — <c>HEAD</c> first, falling back to <c>GET</c> when the target rejects <c>HEAD</c>
/// (405/501). This only answers "can we reach and authenticate" — it does not validate
/// <c>$metadata</c> or the configured records path, the same honesty boundary
/// <c>HttpSourceHealthCheck</c> keeps for the HTTP family (D36).
/// </summary>
public sealed class ODataSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "odata";

    /// <inheritdoc />
    public async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string resourceUrl = ODataConfigProperties.RequireUrl(definition.Properties);
            ODataSourceOptions options = ODataConfigProperties.ReadOptions(definition.Properties);
            HttpClient client = HttpClients.ResolveFrom(services);
            string targetUrl = HttpHealthProbe.CombineUrl(resourceUrl, options.HealthCheckPath);

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
