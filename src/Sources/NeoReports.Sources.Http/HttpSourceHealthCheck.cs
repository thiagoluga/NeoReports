using System.Diagnostics;
using System.Net;
using NeoReports.Core.SourceRegistry;

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
            string targetUrl = CombineUrl(baseUrl, options);

            using HttpResponseMessage response = await ProbeAsync(client, targetUrl, options, cancellationToken).ConfigureAwait(false);

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

    private static async Task<HttpResponseMessage> ProbeAsync(HttpClient client, string targetUrl, HttpSourceOptions options, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Head, targetUrl, options, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            response.Dispose();
            response = await SendAsync(client, HttpMethod.Get, targetUrl, options, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string targetUrl, HttpSourceOptions options, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, targetUrl);
        HttpRequests.ApplyAuth(request, options);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static string CombineUrl(string baseUrl, HttpSourceOptions options)
    {
        if (options.HealthCheckPath is not { } path)
            return baseUrl;

        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        return new Uri(baseUri, path).ToString();
    }
}
