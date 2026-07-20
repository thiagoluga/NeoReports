using System.Net.Http.Headers;
using System.Text.Json;

namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Request-building/error-handling logic shared across the HTTP-family sources' paginated and
/// streaming strategies (ADR D61) — kept in one place to avoid duplicating it, the same
/// duplication-gate discipline earlier Epic P sources followed (e.g. <c>FileSourceHealth</c>).
/// </summary>
public static class HttpRequests
{
    /// <summary>Applies configured static headers, API key, and bearer token to a request.</summary>
    public static void ApplyAuth(HttpRequestMessage request, HttpAuth auth)
    {
        if (auth.StaticHeaders is not null)
        {
            foreach (KeyValuePair<string, string> header in auth.StaticHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (auth.ApiKeyHeaderName is not null)
            request.Headers.TryAddWithoutValidation(auth.ApiKeyHeaderName, auth.ApiKeyValue);

        if (auth.BearerTokenValue is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.BearerTokenValue);
    }

    /// <summary>
    /// Reads the response's <c>Retry-After</c> header (delta-seconds or HTTP-date form), clamped to
    /// non-negative — shared so any HTTP-family failure path (a non-2xx response, or a source-level
    /// failure signaled a different way on an otherwise-successful response, e.g. GraphQL's
    /// <c>200</c>-with-<c>errors</c>) honors the same header the same way.
    /// </summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header)
            return null;

        TimeSpan? retryAfter = header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return retryAfter is { } delay && delay < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
    }

    /// <summary>
    /// Builds an <see cref="HttpSourceException"/> from a non-2xx response, reading
    /// <c>Retry-After</c> before the response body is consumed/disposed (D61 — <c>EnsureSuccessStatusCode</c>
    /// would discard it) and capturing a bounded snippet of the body for the error message.
    /// </summary>
    public static async Task<HttpSourceException> BuildExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        TimeSpan? retryAfter = ReadRetryAfter(response);

        string snippet;
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            snippet = body.Length > 500 ? body[..500] : body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            snippet = string.Empty;
        }

        string message = $"HTTP request failed with status {(int)response.StatusCode} ({response.StatusCode}).";
        if (snippet.Length > 0)
            message += $" Response: {snippet}";

        return new HttpSourceException(response.StatusCode, retryAfter, message);
    }

    /// <summary>
    /// Sends a <c>GET</c> request with <paramref name="auth"/> applied, throws on a non-2xx response
    /// (<see cref="BuildExceptionAsync"/>), and returns the parsed response body (caller disposes) —
    /// the "send, check status, parse JSON" tail shared by every GET-only HTTP-family batch source
    /// (P7a's HubSpot/Airtable; promoted here once that tail was flagged as new-code duplication by
    /// SonarCloud's gate, ADR D65 — the same "promote on a real, evidenced duplicate" discipline as
    /// <c>QueryStrings</c>/<c>HttpHealthProbe</c>/<c>MutableHttpAuth</c>, just driven by tooling
    /// rather than manual code review this time). Sources whose request needs more than <c>GET</c> +
    /// auth (e.g. a POST body) still build their own request and only reuse the lower-level pieces.
    /// </summary>
    public static async Task<JsonDocument> GetJsonAsync(HttpClient client, Uri requestUri, HttpAuth auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyAuth(request, auth);

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await BuildExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
