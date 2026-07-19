using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.OData;

/// <summary>Pagination scheme an OData v4 endpoint uses (ADR D62).</summary>
public enum ODataPaginationStrategy
{
    /// <summary>
    /// Server-driven: follows the response body's <c>@odata.nextLink</c> until absent. The
    /// default — keyset-stable and immune to mid-run inserts, the OData-idiomatic form.
    /// </summary>
    NextLink,

    /// <summary>
    /// Client-driven <c>?$skip=K&amp;$top=M</c> paging. Unstable under concurrent writes to the
    /// feed (a mutating feed can skip/duplicate rows under client-driven offsets, ADR D62's
    /// documented gap) — not the default.
    /// </summary>
    Skip
}

/// <summary>
/// Fluent, mutable options for the OData source (ADR D62) — mirrors the shape of
/// <c>NeoReports.Sources.Http.HttpSourceOptions</c>.
/// </summary>
public sealed class ODataSourceOptions
{
    /// <summary>Pagination strategy. Default <see cref="ODataPaginationStrategy.NextLink"/>.</summary>
    internal ODataPaginationStrategy PaginationStrategy { get; private set; } = ODataPaginationStrategy.NextLink;

    /// <summary>
    /// Dotted path to the array of records within the response body. Default <c>"value"</c> — the
    /// OData v4 standard root array.
    /// </summary>
    internal string RecordsPath { get; private set; } = "value";

    /// <summary>
    /// Optional report-column-name to dotted-JSON-field-path map, for the dynamic (config-driven)
    /// path only — a typed <c>.As&lt;T&gt;()</c> read always matches JSON fields to <c>T</c>'s
    /// properties directly by name.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? FieldMap { get; private set; }

    /// <summary>Static, author-supplied <c>$filter</c> expression, appended to every request as-is.</summary>
    internal string? StaticFilter { get; private set; }

    /// <summary>Static <c>$select</c> expression, appended to every request as-is.</summary>
    internal string? StaticSelect { get; private set; }

    /// <summary>Static <c>$orderby</c> expression, appended to every request as-is.</summary>
    internal string? StaticOrderBy { get; private set; }

    /// <summary>
    /// Explicit <c>$top</c> value used by <see cref="ODataPaginationStrategy.Skip"/>; when unset,
    /// the page size from <c>BatchContext.PageSize</c> is used instead. Meaningless for
    /// <see cref="ODataPaginationStrategy.NextLink"/>, which lets the service control its own page
    /// size.
    /// </summary>
    internal int? TopValue { get; private set; }

    private readonly MutableHttpAuth _auth = new();

    /// <summary>
    /// Path probed by the health check, relative to the resource URL; when unset, the resource URL
    /// itself is probed.
    /// </summary>
    internal string? HealthCheckPath { get; private set; }

    /// <summary>Sets the pagination strategy.</summary>
    public ODataSourceOptions Paginate(ODataPaginationStrategy strategy)
    {
        PaginationStrategy = strategy;
        return this;
    }

    /// <summary>Sets the dotted path to the records array within the response body; defaults to <c>"value"</c>.</summary>
    public ODataSourceOptions RecordsAt(string jsonPath)
    {
        RecordsPath = jsonPath ?? throw new ArgumentNullException(nameof(jsonPath));
        return this;
    }

    /// <summary>
    /// Sets a static, author-supplied <c>$filter</c> expression, appended to every request as-is —
    /// the 90% path (ADR D62). Structured preview-filter pushdown (<see cref="ODataFilterTranslator"/>)
    /// ANDs its generated expression onto this one when both are present.
    /// </summary>
    public ODataSourceOptions Filter(string odataFilterExpression)
    {
        StaticFilter = odataFilterExpression ?? throw new ArgumentNullException(nameof(odataFilterExpression));
        return this;
    }

    /// <summary>Sets a static <c>$select</c> expression (comma-separated field names), appended to every request as-is.</summary>
    public ODataSourceOptions Select(string commaSeparatedFields)
    {
        StaticSelect = commaSeparatedFields ?? throw new ArgumentNullException(nameof(commaSeparatedFields));
        return this;
    }

    /// <summary>Sets a static <c>$orderby</c> expression, appended to every request as-is.</summary>
    public ODataSourceOptions OrderBy(string expression)
    {
        StaticOrderBy = expression ?? throw new ArgumentNullException(nameof(expression));
        return this;
    }

    /// <summary>
    /// Sets the explicit <c>$top</c> value used by <see cref="ODataPaginationStrategy.Skip"/>; when
    /// unset, <c>BatchContext.PageSize</c> is used instead. Meaningless for
    /// <see cref="ODataPaginationStrategy.NextLink"/>.
    /// </summary>
    public ODataSourceOptions Top(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        TopValue = pageSize;
        return this;
    }

    /// <summary>Maps report columns to dotted JSON field paths (dynamic path only).</summary>
    public ODataSourceOptions FieldsFrom(IReadOnlyDictionary<string, string> fieldMap)
    {
        FieldMap = fieldMap ?? throw new ArgumentNullException(nameof(fieldMap));
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public ODataSourceOptions Header(string name, string value)
    {
        _auth.Header(name, value);
        return this;
    }

    /// <summary>Sends a static API key as a request header (static auth only; OAuth2 is deferred, ADR D61/D62).</summary>
    public ODataSourceOptions ApiKey(string headerName, string value)
    {
        _auth.ApiKey(headerName, value);
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (static auth only; ADR D61/D62).</summary>
    public ODataSourceOptions Bearer(string token)
    {
        _auth.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the resource URL; defaults to the resource URL itself.</summary>
    public ODataSourceOptions HealthCheckAt(string path)
    {
        HealthCheckPath = path ?? throw new ArgumentNullException(nameof(path));
        return this;
    }

    /// <summary>Projects this instance's auth-related fields into the shared, source-agnostic <see cref="HttpAuth"/> shape.</summary>
    internal HttpAuth ToAuth() => _auth.ToAuth();
}
