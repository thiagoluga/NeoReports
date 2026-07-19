namespace NeoReports.Sources.Http.Common;

/// <summary>
/// The static, per-request auth material an HTTP-family source applies to every request (ADR D61)
/// — a static API key header, a static bearer token, and/or arbitrary static headers. Deliberately
/// data-only (no OAuth2/token-refresh flow — P4a scope, deferred per ADR D61); callers project their
/// own options type into this shape and pass it to <see cref="HttpRequests.ApplyAuth"/>.
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
