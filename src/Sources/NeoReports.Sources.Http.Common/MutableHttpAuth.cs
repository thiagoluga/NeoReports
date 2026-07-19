namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Mutable auth-field bookkeeping shared by every HTTP-family source's fluent options class (ADR
/// D61/D62/D63) — the four static-auth fields, their setters, and the projection into
/// <see cref="HttpAuth"/> were identical, independently-maintained copies across three packages
/// (<c>HttpSourceOptions</c>, <c>ODataSourceOptions</c>, <c>GraphQlSourceOptions</c>) before being
/// promoted here; each now holds one <see cref="MutableHttpAuth"/> by composition and forwards its
/// own <c>Header</c>/<c>ApiKey</c>/<c>Bearer</c>/<c>ToAuth</c> methods to it, keeping its own fluent
/// return type (<c>this</c>) for the builder chain.
/// </summary>
public sealed class MutableHttpAuth
{
    /// <summary>Static request headers applied to every request.</summary>
    public IReadOnlyDictionary<string, string>? StaticHeaders { get; private set; }

    /// <summary>Header name an API key is sent under, when configured.</summary>
    public string? ApiKeyHeaderName { get; private set; }

    /// <summary>API key value, when configured.</summary>
    public string? ApiKeyValue { get; private set; }

    /// <summary>Bearer token value, when configured (<c>Authorization: Bearer &lt;token&gt;</c>).</summary>
    public string? BearerTokenValue { get; private set; }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public void Header(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var headers = new Dictionary<string, string>(StaticHeaders ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            [name] = value
        };
        StaticHeaders = headers;
    }

    /// <summary>Sends a static API key as a request header (static auth only; OAuth2 is out of scope, ADR D61).</summary>
    public void ApiKey(string headerName, string value)
    {
        ApiKeyHeaderName = headerName ?? throw new ArgumentNullException(nameof(headerName));
        ApiKeyValue = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (static auth only; ADR D61).</summary>
    public void Bearer(string token)
    {
        BearerTokenValue = token ?? throw new ArgumentNullException(nameof(token));
    }

    /// <summary>Projects the current fields into the shared, source-agnostic <see cref="HttpAuth"/> shape.</summary>
    public HttpAuth ToAuth() => new(StaticHeaders, ApiKeyHeaderName, ApiKeyValue, BearerTokenValue);
}
