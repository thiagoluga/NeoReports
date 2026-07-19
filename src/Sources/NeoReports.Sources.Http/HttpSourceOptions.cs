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

    /// <summary>Static request headers applied to every request.</summary>
    internal IReadOnlyDictionary<string, string>? StaticHeaders { get; private set; }

    /// <summary>Header name an API key is sent under, when configured.</summary>
    internal string? ApiKeyHeaderName { get; private set; }

    /// <summary>API key value, when configured.</summary>
    internal string? ApiKeyValue { get; private set; }

    /// <summary>Bearer token value, when configured (<c>Authorization: Bearer &lt;token&gt;</c>).</summary>
    internal string? BearerTokenValue { get; private set; }

    /// <summary>
    /// Path probed by the health check, relative to the source's base URL; when unset, the base URL
    /// itself is probed.
    /// </summary>
    internal string? HealthCheckPath { get; private set; }

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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var headers = new Dictionary<string, string>(StaticHeaders ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            [name] = value
        };
        StaticHeaders = headers;
        return this;
    }

    /// <summary>Sends a static API key as a request header (P4a — static auth only; OAuth2 is deferred, ADR D61).</summary>
    public HttpSourceOptions ApiKey(string headerName, string value)
    {
        ApiKeyHeaderName = headerName ?? throw new ArgumentNullException(nameof(headerName));
        ApiKeyValue = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (P4a — static auth only; ADR D61).</summary>
    public HttpSourceOptions Bearer(string token)
    {
        BearerTokenValue = token ?? throw new ArgumentNullException(nameof(token));
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the base URL; defaults to the base URL itself.</summary>
    public HttpSourceOptions HealthCheckAt(string path)
    {
        HealthCheckPath = path ?? throw new ArgumentNullException(nameof(path));
        return this;
    }

    /// <summary>Projects this instance's auth-related fields into the shared, source-agnostic <see cref="HttpAuth"/> shape.</summary>
    internal HttpAuth ToAuth() => new(StaticHeaders, ApiKeyHeaderName, ApiKeyValue, BearerTokenValue);
}
