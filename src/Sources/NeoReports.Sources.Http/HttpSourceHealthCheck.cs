using System.Diagnostics;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>
/// On-demand health check for a registered HTTP source (ADR D42/D61, <c>type: "http"</c>). Probes
/// the configured <c>healthCheckPath</c> (or the base URL itself when unset) with the same
/// configured auth — <c>HEAD</c> first, falling back to <c>GET</c> when the target rejects <c>HEAD</c>
/// (405/501). This only answers "can we reach and authenticate" — it does not validate the
/// configured records path or pagination shape, the same honesty boundary
/// <c>FileSourceHealth</c>'s "can this be read right now" keeps for file sources (D36).
/// </summary>
public sealed class HttpSourceHealthCheck : ISourceHealthCheck
{
    /// <inheritdoc />
    public string Type => "http";

    /// <inheritdoc />
    public async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string baseUrl = HttpConfigProperties.RequireUrl(definition.Properties);
            HttpSourceOptions options = HttpConfigProperties.ReadOptions(definition.Properties);
            HttpClient client = HttpClients.ResolveFrom(services);
            string targetUrl = HttpHealthProbe.CombineUrl(baseUrl, options.HealthCheckPath);

            OAuth2ClientCredentialsProvider? oauth2Provider = HttpOAuth2.CreateProvider(client, options);
            HttpAuth auth = await HttpOAuth2.ResolveAuthAsync(options, oauth2Provider, cancellationToken).ConfigureAwait(false);
            using HttpResponseMessage response = await HttpHealthProbe.ProbeAsync(client, targetUrl, auth, cancellationToken).ConfigureAwait(false);

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
