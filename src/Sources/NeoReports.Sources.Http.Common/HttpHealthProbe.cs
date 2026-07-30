using System.Diagnostics;
using System.Net;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Shared "can we reach and authenticate" health-check probe for the HTTP-family sources (ADR
/// D61/D62): <c>HEAD</c> first, falling back to <c>GET</c> when the target rejects <c>HEAD</c>
/// (405/501) with the same configured auth. Deliberately does not validate a source's records path
/// or query shape — reachability/auth only, matching <c>FileSourceHealth</c>'s honesty boundary (D36).
/// </summary>
public static class HttpHealthProbe
{
    /// <summary>Probes <paramref name="targetUrl"/>, falling back from <c>HEAD</c> to <c>GET</c> on 405/501.</summary>
    public static async Task<HttpResponseMessage> ProbeAsync(HttpClient client, string targetUrl, HttpAuth auth, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Head, targetUrl, auth, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            response.Dispose();
            response = await SendAsync(client, HttpMethod.Get, targetUrl, auth, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Sends a single probe request with the configured auth applied. <paramref name="content"/> is
    /// optional — a <c>POST</c>-only, single-endpoint transport (e.g. GraphQL's probe query) supplies
    /// a body here instead of using <see cref="ProbeAsync"/>'s <c>HEAD</c>/<c>GET</c> fallback, which
    /// doesn't apply to it.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string targetUrl, HttpAuth auth, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, targetUrl) { Content = content };
        HttpRequests.ApplyAuth(request, auth);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the URL to probe: <paramref name="healthCheckPath"/> relative to <paramref name="baseUrl"/>, or <paramref name="baseUrl"/> itself when unset.</summary>
    public static string CombineUrl(string baseUrl, string? healthCheckPath)
    {
        if (healthCheckPath is null)
            return baseUrl;

        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        return new Uri(baseUri, healthCheckPath).ToString();
    }

    /// <summary>
    /// Runs a full <see cref="ISourceHealthCheck.CheckAsync"/> implementation: times <paramref name="probe"/>
    /// (which reads the source's own config properties and issues the request — typically
    /// <see cref="ProbeAsync"/> or <see cref="SendAsync"/>), and converts the outcome into a
    /// <see cref="SourceHealthResult"/> — success/non-2xx/exception, all caught here — the "check
    /// status, build result, catch" tail shared by every HTTP-family health check (promoted once
    /// SonarCloud's new-code duplication gate flagged it as a real duplicate across P7a's HubSpot and
    /// Airtable health checks, ADR D65; the same "promote on a real, evidenced duplicate" discipline
    /// as <c>QueryStrings</c>/<c>MutableHttpAuth</c>, just tooling-driven this time). A health check
    /// whose config-property reads should themselves count toward the caught/reported failure passes
    /// them inside <paramref name="probe"/>, not before calling this method.
    /// </summary>
    public static async Task<SourceHealthResult> CheckAsync(Func<Task<HttpResponseMessage>> probe, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await probe().ConfigureAwait(false);
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
