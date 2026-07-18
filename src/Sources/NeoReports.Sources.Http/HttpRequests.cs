using System.Net.Http.Headers;

namespace NeoReports.Sources.Http;

/// <summary>
/// Request-building/error-handling logic shared by <see cref="HttpBatchSource{T}"/> and
/// <see cref="HttpStreamingSource{T}"/> (ADR D61) — kept in one place to avoid duplicating it
/// across the two strategies, the same duplication-gate discipline earlier Epic P sources followed
/// (e.g. <c>FileSourceHealth</c>).
/// </summary>
internal static class HttpRequests
{
    /// <summary>Applies configured static headers, API key, and bearer token to a request.</summary>
    public static void ApplyAuth(HttpRequestMessage request, HttpSourceOptions options)
    {
        if (options.StaticHeaders is not null)
        {
            foreach (KeyValuePair<string, string> header in options.StaticHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (options.ApiKeyHeaderName is not null)
            request.Headers.TryAddWithoutValidation(options.ApiKeyHeaderName, options.ApiKeyValue);

        if (options.BearerTokenValue is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerTokenValue);
    }

    /// <summary>
    /// Builds an <see cref="HttpSourceException"/> from a non-2xx response, reading
    /// <c>Retry-After</c> before the response body is consumed/disposed (D61 — <c>EnsureSuccessStatusCode</c>
    /// would discard it) and capturing a bounded snippet of the body for the error message.
    /// </summary>
    public static async Task<HttpSourceException> BuildExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter is { } header)
        {
            retryAfter = header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            if (retryAfter is { } delay && delay < TimeSpan.Zero)
                retryAfter = TimeSpan.Zero;
        }

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
}
