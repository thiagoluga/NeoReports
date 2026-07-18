namespace NeoReports.Sources.Http;

/// <summary>
/// Resolves the <see cref="HttpClient"/> an HTTP source uses (ADR D61): a DI-registered instance
/// first — the same "resolve a DI-registered client first, else self-manage" precedent
/// <c>FileSourceProperties</c>/<c>FileSourceHealth</c> established for <c>IAmazonS3</c> — falling
/// back to one process-wide shared instance. No <c>Microsoft.Extensions.Http</c>/
/// <c>IHttpClientFactory</c> dependency: the source builds absolute request URIs and applies
/// headers per-request rather than presetting <c>BaseAddress</c>/<c>DefaultRequestHeaders</c> on the
/// client, so nothing about the client itself needs to vary per source configuration — a single
/// shared, pooled <see cref="HttpClient"/> is exactly as safe here as the documented "share one
/// HttpClient for the app's lifetime" alternative to the factory.
/// </summary>
internal static class HttpClients
{
    // Cookies disabled: the default HttpClientHandler's CookieContainer is scoped to this one
    // shared instance, so without this, a Set-Cookie response from one report's source would
    // silently be replayed on a later, unrelated report's requests to the same host (a real
    // cross-source leak, not just wasted memory) — the source already applies auth per-request via
    // headers, so a shared cookie jar serves no purpose here.
    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient(new HttpClientHandler { UseCookies = false }));

    /// <summary>The process-wide shared fallback client.</summary>
    public static HttpClient Default => Shared.Value;

    /// <summary>Resolves a DI-registered <see cref="HttpClient"/>, or <see cref="Default"/> when none is registered.</summary>
    public static HttpClient ResolveFrom(IServiceProvider? services) =>
        (services?.GetService(typeof(HttpClient)) as HttpClient) ?? Default;
}
