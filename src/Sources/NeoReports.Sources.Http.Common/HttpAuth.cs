namespace NeoReports.Sources.Http.Common;

/// <summary>
/// The (possibly per-request-resolved) auth material an HTTP-family source applies to one request
/// (ADR D61) — a static API key header, a bearer token, and/or arbitrary static headers. Deliberately
/// stays a plain data snapshot regardless of where the bearer token came from: a caller with a
/// dynamically-refreshed token (e.g. <c>NeoReports.Sources.Http</c>'s OAuth2 client-credentials
/// support, P4b/ADR D68) resolves it first and passes the current value in here, rather than this
/// type growing any async/refresh logic of its own. Callers project their own options type into this
/// shape and pass it to <see cref="HttpRequests.ApplyAuth"/>.
/// </summary>
/// <param name="StaticHeaders">Static request headers applied to every request.</param>
/// <param name="ApiKeyHeaderName">Header name an API key is sent under, when configured.</param>
/// <param name="ApiKeyValue">API key value, when configured.</param>
/// <param name="BearerTokenValue">Bearer token value, when configured (<c>Authorization: Bearer &lt;token&gt;</c>).</param>
public sealed record HttpAuth(
    IReadOnlyDictionary<string, string>? StaticHeaders = null,
    string? ApiKeyHeaderName = null,
    string? ApiKeyValue = null,
    string? BearerTokenValue = null);
