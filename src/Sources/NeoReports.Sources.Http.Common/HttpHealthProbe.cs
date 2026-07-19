using System.Net;

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
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Head, targetUrl, auth, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            response.Dispose();
            response = await SendAsync(client, HttpMethod.Get, targetUrl, auth, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>Sends a single probe request with the configured auth applied.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string targetUrl, HttpAuth auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, targetUrl);
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
}
