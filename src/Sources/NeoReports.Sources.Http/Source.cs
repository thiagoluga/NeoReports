using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>Fluent entry point for a generic HTTP/REST source (ADR D61).</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring an HTTP/REST source. Defaults to <see cref="HttpPaginationStrategy.None"/>
    /// (the whole response is a single JSON array) — call <see cref="HttpSourceBuilder.Paginate"/>
    /// to select a paginated strategy.
    /// </summary>
    /// <param name="baseUrl">The endpoint's base URL.</param>
    /// <param name="client">An explicit <see cref="HttpClient"/> (caller owns its lifetime), or <c>null</c> to use a lazily-created shared instance.</param>
    public static HttpSourceBuilder Http(string baseUrl, HttpClient? client = null) => new(baseUrl, client);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for an HTTP/REST source, before the row type is chosen.</summary>
public sealed class HttpSourceBuilder
{
    private static readonly JsonSerializerOptions TypedDeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _baseUrl;
    private readonly HttpClient? _client;
    private readonly HttpSourceOptions _options = new();

    internal HttpSourceBuilder(string baseUrl, HttpClient? client)
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? throw new ArgumentException("Base URL must be provided.", nameof(baseUrl)) : baseUrl;
        _client = client;
    }

    /// <summary>Sets the pagination strategy.</summary>
    public HttpSourceBuilder Paginate(HttpPaginationStrategy strategy)
    {
        _options.Paginate(strategy);
        return this;
    }

    /// <summary>Sets the dotted path to the records array within the response body; empty means the response body itself is the array (the default).</summary>
    public HttpSourceBuilder RecordsAt(string jsonPath)
    {
        _options.RecordsAt(jsonPath);
        return this;
    }

    /// <summary>Configures the <see cref="HttpPaginationStrategy.Cursor"/> strategy's token field/parameter.</summary>
    public HttpSourceBuilder CursorField(string responsePath, string requestParam = "cursor")
    {
        _options.CursorField(responsePath, requestParam);
        return this;
    }

    /// <summary>Configures the <see cref="HttpPaginationStrategy.Page"/> strategy's query parameters.</summary>
    public HttpSourceBuilder PageParams(string pageParam = "page", string pageSizeParam = "pageSize", int startPage = 1)
    {
        _options.PageParams(pageParam, pageSizeParam, startPage);
        return this;
    }

    /// <summary>Configures the <see cref="HttpPaginationStrategy.Offset"/> strategy's query parameters.</summary>
    public HttpSourceBuilder OffsetParams(string offsetParam = "offset", string limitParam = "pageSize")
    {
        _options.OffsetParams(offsetParam, limitParam);
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public HttpSourceBuilder Header(string name, string value)
    {
        _options.Header(name, value);
        return this;
    }

    /// <summary>Sends a static API key as a request header (ADR D61). OAuth2 client-credentials is available separately via <see cref="OAuth2ClientCredentials"/> (P4b, ADR D68).</summary>
    public HttpSourceBuilder ApiKey(string headerName, string value)
    {
        _options.ApiKey(headerName, value);
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (ADR D61). Mutually exclusive with <see cref="OAuth2ClientCredentials"/>.</summary>
    public HttpSourceBuilder Bearer(string token)
    {
        _options.Bearer(token);
        return this;
    }

    /// <summary>
    /// Authenticates via the OAuth2 client-credentials grant (P4b, ADR D68) instead of a static
    /// bearer token — a fresh access token is fetched (and transparently refreshed before expiry) on
    /// every request. Mutually exclusive with <see cref="Bearer"/>.
    /// </summary>
    public HttpSourceBuilder OAuth2ClientCredentials(string tokenEndpoint, string clientId, string clientSecret, string? scope = null)
    {
        _options.OAuth2ClientCredentials(tokenEndpoint, clientId, clientSecret, scope);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the base URL; defaults to the base URL itself.</summary>
    public HttpSourceBuilder HealthCheckAt(string path)
    {
        _options.HealthCheckAt(path);
        return this;
    }

    /// <summary>
    /// Completes the source, materializing each record as <typeparamref name="T"/> via
    /// <see cref="JsonSerializer"/> directly (case-insensitive property matching) — a configured
    /// field map only applies to the dynamic (config-driven) path; the typed path expects JSON
    /// field names to already match <typeparamref name="T"/>'s property names.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IBatchSource<T> As<T>()
    {
        HttpClient client = _client ?? HttpClients.Default;
        T Materialize(JsonElement element) => element.Deserialize<T>(TypedDeserializeOptions)!;

        if (_options.PaginationStrategy == HttpPaginationStrategy.None)
        {
            var streaming = new HttpStreamingSource<T>(client, _baseUrl, _options, Source.Placeholder, Materialize);
            return new StreamingToBatchSource<T>(streaming);
        }

        return new HttpBatchSource<T>(client, _baseUrl, _options, Source.Placeholder, Materialize);
    }
}
