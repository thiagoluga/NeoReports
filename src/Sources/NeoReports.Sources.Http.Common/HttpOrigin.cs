namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Cross-origin check for HTTP-family sources that follow a server-supplied next-page URL (ADR
/// D61) — e.g. a <c>Link: rel="next"</c> header. A response can legitimately point elsewhere (a
/// signed CDN URL), but the caller's configured API key/bearer token/headers are for the
/// configured host, not whatever a response says to fetch next. Refusing to replay those
/// credentials cross-origin mirrors the "don't forward Authorization across a different authority"
/// rule <see cref="HttpClient"/> itself applies to redirects — a security-review fix (ADR D61); do
/// not weaken this comparison.
/// </summary>
public static class HttpOrigin
{
    /// <summary>Whether <paramref name="requestUri"/> shares scheme, host, and port with <paramref name="baseUri"/>.</summary>
    public static bool IsSameOrigin(Uri requestUri, Uri baseUri) =>
        string.Equals(requestUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(requestUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
        && requestUri.Port == baseUri.Port;
}
