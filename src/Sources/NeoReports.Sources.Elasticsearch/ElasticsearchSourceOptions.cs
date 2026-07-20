using System.Text.Json;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// Fluent, mutable options for the Elasticsearch/OpenSearch source (ADR D64) — mirrors the shape of
/// <c>NeoReports.Sources.OData.ODataSourceOptions</c>.
/// </summary>
public sealed class ElasticsearchSourceOptions
{
    /// <summary>
    /// Static, author-supplied Elasticsearch Query DSL object, appended to every request as-is.
    /// Defaults to <see cref="ElasticsearchQueries.MatchAll"/> when never set.
    /// </summary>
    internal JsonElement? StaticQuery { get; private set; }

    /// <summary>
    /// Required sort specification (a JSON array of Elasticsearch sort clauses) — <c>search_after</c>
    /// paging has no default; the configured sort must include a tiebreaker producing a total order
    /// (conventionally <c>{"_id":"asc"}</c>), an uncommunicated caller responsibility (ADR D64).
    /// </summary>
    internal JsonElement? SortSpec { get; private set; }

    /// <summary>
    /// Optional report-column-name to dotted-JSON-field-path map, for the dynamic (config-driven)
    /// path only — a typed <c>.As&lt;T&gt;()</c> read always matches JSON fields to <c>T</c>'s
    /// properties directly by name.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? FieldMap { get; private set; }

    private readonly MutableHttpAuth _auth = new();

    /// <summary>
    /// Path probed by the health check, relative to <c>{url}/{index}</c>; when unset, <c>{url}/{index}</c>
    /// itself is probed.
    /// </summary>
    internal string? HealthCheckPath { get; private set; }

    /// <summary>Sets a static Elasticsearch Query DSL expression (a JSON object), appended to every request as-is.</summary>
    public ElasticsearchSourceOptions Query(string queryDslJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryDslJson);
        JsonElement parsed = JsonDocument.Parse(queryDslJson).RootElement.Clone();
        if (parsed.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The Elasticsearch query must be a JSON object.", nameof(queryDslJson));

        StaticQuery = parsed;
        return this;
    }

    /// <summary>
    /// Sets the required sort specification (a JSON array of Elasticsearch sort clauses, e.g.
    /// <c>[{"createdAt":"asc"},{"_id":"asc"}]</c>) — must end in a tiebreaker producing a total
    /// order for <c>search_after</c> paging to be safe (ADR D64, not enforced server-side).
    /// </summary>
    public ElasticsearchSourceOptions Sort(string sortDslJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sortDslJson);
        JsonElement parsed = JsonDocument.Parse(sortDslJson).RootElement.Clone();
        if (parsed.ValueKind != JsonValueKind.Array || parsed.GetArrayLength() == 0)
            throw new ArgumentException("The Elasticsearch sort must be a non-empty JSON array.", nameof(sortDslJson));

        SortSpec = parsed;
        return this;
    }

    /// <summary>Maps report columns to dotted JSON field paths (dynamic path only).</summary>
    public ElasticsearchSourceOptions FieldsFrom(IReadOnlyDictionary<string, string> fieldMap)
    {
        FieldMap = fieldMap ?? throw new ArgumentNullException(nameof(fieldMap));
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public ElasticsearchSourceOptions Header(string name, string value)
    {
        _auth.Header(name, value);
        return this;
    }

    /// <summary>Sends a static API key as a request header (static auth only; OAuth2 is deferred, ADR D61/D64).</summary>
    public ElasticsearchSourceOptions ApiKey(string headerName, string value)
    {
        _auth.ApiKey(headerName, value);
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (static auth only; ADR D61/D64).</summary>
    public ElasticsearchSourceOptions Bearer(string token)
    {
        _auth.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to <c>{url}/{index}</c>; defaults to <c>{url}/{index}</c> itself.</summary>
    public ElasticsearchSourceOptions HealthCheckAt(string path)
    {
        HealthCheckPath = path ?? throw new ArgumentNullException(nameof(path));
        return this;
    }

    /// <summary>Projects this instance's auth-related fields into the shared, source-agnostic <see cref="HttpAuth"/> shape.</summary>
    internal HttpAuth ToAuth() => _auth.ToAuth();
}
