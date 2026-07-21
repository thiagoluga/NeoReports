using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>Pagination scheme a REST endpoint uses (ADR D61).</summary>
public enum HttpPaginationStrategy
{
    /// <summary>A continuation token read from the response body is sent back on the next request.</summary>
    Cursor,

    /// <summary>The RFC 5988/8288 <c>Link: &lt;url&gt;; rel="next"</c> response header carries the next page's URL.</summary>
    LinkHeader,

    /// <summary>A <c>?page=N</c>-style query parameter, advanced by one each page.</summary>
    Page,

    /// <summary>A <c>?offset=N&amp;limit=M</c>-style query parameter pair.</summary>
    Offset,

    /// <summary>The whole result set is a single JSON array in one response — no pagination at all.</summary>
    None
}

/// <summary>
/// Fluent, mutable options for the HTTP source (ADR D61) — mirrors the shape of
/// <c>NeoReports.Sources.Csv.CsvReaderOptions</c> (fluent setter methods returning <c>this</c>).
/// </summary>
public sealed class HttpSourceOptions
{
    /// <summary>Pagination strategy. Default <see cref="HttpPaginationStrategy.None"/>.</summary>
    internal HttpPaginationStrategy PaginationStrategy { get; private set; } = HttpPaginationStrategy.None;

    /// <summary>
    /// Dotted path to the array of records within the response body (e.g. <c>"data.items"</c>);
    /// empty means the response body itself is the array.
    /// </summary>
    internal string RecordsPath { get; private set; } = "";

    /// <summary>
    /// Optional report-column-name to dotted-JSON-field-path map, for the dynamic (config-driven)
    /// path only — a typed <c>.As&lt;T&gt;()</c> read always matches JSON fields to <c>T</c>'s
    /// properties directly by name.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? FieldMap { get; private set; }

    /// <summary>Dotted path, within the response body, to the next-page continuation token (<see cref="HttpPaginationStrategy.Cursor"/>).</summary>
    internal string CursorResponsePath { get; private set; } = "nextCursor";

    /// <summary>Query parameter the continuation token is sent back as (<see cref="HttpPaginationStrategy.Cursor"/>).</summary>
    internal string CursorRequestParam { get; private set; } = "cursor";

    /// <summary>Query parameter carrying the page number (<see cref="HttpPaginationStrategy.Page"/>).</summary>
    internal string PageParam { get; private set; } = "page";

    /// <summary>Query parameter carrying the page size (<see cref="HttpPaginationStrategy.Page"/> and <see cref="HttpPaginationStrategy.Offset"/>).</summary>
    internal string PageSizeParam { get; private set; } = "pageSize";

    /// <summary>First page number (<see cref="HttpPaginationStrategy.Page"/>). Default <c>1</c>.</summary>
    internal int StartPage { get; private set; } = 1;

    /// <summary>Query parameter carrying the offset (<see cref="HttpPaginationStrategy.Offset"/>).</summary>
    internal string OffsetParam { get; private set; } = "offset";

    private readonly MutableHttpAuth _auth = new();

    /// <summary>
    /// Path probed by the health check, relative to the source's base URL; when unset, the base URL
    /// itself is probed.
    /// </summary>
    internal string? HealthCheckPath { get; private set; }

    /// <summary>OAuth2 client-credentials token endpoint (P4b, ADR D68), when configured instead of a static <see cref="Bearer"/> token.</summary>
    internal string? OAuth2TokenEndpoint { get; private set; }

    /// <summary>OAuth2 client id (P4b).</summary>
    internal string? OAuth2ClientId { get; private set; }

    /// <summary>OAuth2 client secret (P4b).</summary>
    internal string? OAuth2ClientSecret { get; private set; }

    /// <summary>OAuth2 scope, when configured (P4b).</summary>
    internal string? OAuth2Scope { get; private set; }

    /// <summary>Sets the pagination strategy.</summary>
    public HttpSourceOptions Paginate(HttpPaginationStrategy strategy)
    {
        PaginationStrategy = strategy;
        return this;
    }

    /// <summary>Sets the dotted path to the records array within the response body.</summary>
    public HttpSourceOptions RecordsAt(string jsonPath)
    {
        RecordsPath = jsonPath ?? throw new ArgumentNullException(nameof(jsonPath));
        return this;
    }

    /// <summary>Maps report columns to dotted JSON field paths (dynamic path only).</summary>
    public HttpSourceOptions FieldsFrom(IReadOnlyDictionary<string, string> fieldMap)
    {
        FieldMap = fieldMap ?? throw new ArgumentNullException(nameof(fieldMap));
        return this;
    }

    /// <summary>Configures the <see cref="HttpPaginationStrategy.Cursor"/> strategy's token field/parameter.</summary>
    public HttpSourceOptions CursorField(string responsePath, string requestParam = "cursor")
    {
        CursorResponsePath = responsePath ?? throw new ArgumentNullException(nameof(responsePath));
        CursorRequestParam = requestParam ?? throw new ArgumentNullException(nameof(requestParam));
        return this;
    }

    /// <summary>Configures the <see cref="HttpPaginationStrategy.Page"/> strategy's query parameters.</summary>
    public HttpSourceOptions PageParams(string pageParam = "page", string pageSizeParam = "pageSize", int startPage = 1)
    {
        PageParam = pageParam ?? throw new ArgumentNullException(nameof(pageParam));
        PageSizeParam = pageSizeParam ?? throw new ArgumentNullException(nameof(pageSizeParam));
        StartPage = startPage;
        return this;
    }

    /// <summary>Configures the <see cref="HttpPaginationStrategy.Offset"/> strategy's query parameters.</summary>
    public HttpSourceOptions OffsetParams(string offsetParam = "offset", string limitParam = "pageSize")
    {
        OffsetParam = offsetParam ?? throw new ArgumentNullException(nameof(offsetParam));
        PageSizeParam = limitParam ?? throw new ArgumentNullException(nameof(limitParam));
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public HttpSourceOptions Header(string name, string value)
    {
        _auth.Header(name, value);
        return this;
    }

    /// <summary>Sends a static API key as a request header (ADR D61). OAuth2 client-credentials is available separately via <see cref="OAuth2ClientCredentials"/> (P4b, ADR D68).</summary>
    public HttpSourceOptions ApiKey(string headerName, string value)
    {
        _auth.ApiKey(headerName, value);
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (ADR D61). Mutually exclusive with <see cref="OAuth2ClientCredentials"/>.</summary>
    public HttpSourceOptions Bearer(string token)
    {
        if (OAuth2TokenEndpoint is not null)
            throw new ArgumentException("Bearer and OAuth2ClientCredentials are mutually exclusive; configure only one.", nameof(token));

        _auth.Bearer(token);
        return this;
    }

    /// <summary>
    /// Authenticates via the OAuth2 client-credentials grant (P4b, ADR D68) instead of a static
    /// bearer token — a fresh access token is fetched (and transparently refreshed before expiry) on
    /// every request. Mutually exclusive with <see cref="Bearer"/>.
    /// </summary>
    /// <param name="tokenEndpoint">The OAuth2 token endpoint URL.</param>
    /// <param name="clientId">The OAuth2 client id.</param>
    /// <param name="clientSecret">The OAuth2 client secret.</param>
    /// <param name="scope">Optional space-delimited scope string.</param>
    public HttpSourceOptions OAuth2ClientCredentials(string tokenEndpoint, string clientId, string clientSecret, string? scope = null)
    {
        if (_auth.BearerTokenValue is not null)
            throw new ArgumentException("OAuth2ClientCredentials and Bearer are mutually exclusive; configure only one.", nameof(tokenEndpoint));

        OAuth2TokenEndpoint = string.IsNullOrWhiteSpace(tokenEndpoint) ? throw new ArgumentException("Token endpoint must be provided.", nameof(tokenEndpoint)) : tokenEndpoint;
        OAuth2ClientId = string.IsNullOrWhiteSpace(clientId) ? throw new ArgumentException("Client id must be provided.", nameof(clientId)) : clientId;
        OAuth2ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? throw new ArgumentException("Client secret must be provided.", nameof(clientSecret)) : clientSecret;
        OAuth2Scope = scope;
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the base URL; defaults to the base URL itself.</summary>
    public HttpSourceOptions HealthCheckAt(string path)
    {
        HealthCheckPath = path ?? throw new ArgumentNullException(nameof(path));
        return this;
    }

    /// <summary>Projects this instance's static auth-related fields into the shared, source-agnostic <see cref="HttpAuth"/> shape. Does not include an OAuth2 access token — see <see cref="HttpOAuth2.ResolveAuthAsync"/>.</summary>
    internal HttpAuth ToAuth() => _auth.ToAuth();
}
