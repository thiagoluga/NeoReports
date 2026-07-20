using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>Fluent entry point for an Elasticsearch/OpenSearch source (ADR D64).</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring an Elasticsearch/OpenSearch source. <see cref="ElasticsearchSourceBuilder.Sort"/>
    /// must be called before <see cref="ElasticsearchSourceBuilder.As{T}"/> — <c>search_after</c>
    /// keyset paging has no default sort to fall back on.
    /// </summary>
    /// <param name="url">The Elasticsearch/OpenSearch base URL.</param>
    /// <param name="index">The index (or alias/pattern) to search.</param>
    /// <param name="client">An explicit <see cref="HttpClient"/> (caller owns its lifetime), or <c>null</c> to use a lazily-created shared instance.</param>
    public static ElasticsearchSourceBuilder Elasticsearch(string url, string index, HttpClient? client = null) => new(url, index, client);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for an Elasticsearch/OpenSearch source, before the row type is chosen.</summary>
public sealed class ElasticsearchSourceBuilder
{
    private static readonly JsonSerializerOptions TypedDeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _url;
    private readonly string _index;
    private readonly HttpClient? _client;
    private readonly ElasticsearchSourceOptions _options = new();

    internal ElasticsearchSourceBuilder(string url, string index, HttpClient? client)
    {
        _url = string.IsNullOrWhiteSpace(url) ? throw new ArgumentException("URL must be provided.", nameof(url)) : url;
        _index = string.IsNullOrWhiteSpace(index) ? throw new ArgumentException("Index must be provided.", nameof(index)) : index;
        _client = client;
    }

    /// <summary>Sets a static Elasticsearch Query DSL expression (a JSON object), appended to every request as-is.</summary>
    public ElasticsearchSourceBuilder Query(string queryDslJson)
    {
        _options.Query(queryDslJson);
        return this;
    }

    /// <summary>
    /// Sets the required sort specification (a JSON array of Elasticsearch sort clauses, e.g.
    /// <c>[{"createdAt":"asc"},{"_id":"asc"}]</c>) — must end in a tiebreaker producing a total
    /// order for <c>search_after</c> paging to be safe (ADR D64, not enforced server-side).
    /// </summary>
    public ElasticsearchSourceBuilder Sort(string sortDslJson)
    {
        _options.Sort(sortDslJson);
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public ElasticsearchSourceBuilder Header(string name, string value)
    {
        _options.Header(name, value);
        return this;
    }

    /// <summary>Sends a static API key as a request header (static auth only; OAuth2 is deferred, ADR D61/D64).</summary>
    public ElasticsearchSourceBuilder ApiKey(string headerName, string value)
    {
        _options.ApiKey(headerName, value);
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (static auth only; ADR D61/D64).</summary>
    public ElasticsearchSourceBuilder Bearer(string token)
    {
        _options.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to <c>{url}/{index}</c>; defaults to <c>{url}/{index}</c> itself.</summary>
    public ElasticsearchSourceBuilder HealthCheckAt(string path)
    {
        _options.HealthCheckAt(path);
        return this;
    }

    /// <summary>
    /// Completes the source, materializing each hit's <c>_source</c> as <typeparamref name="T"/> via
    /// <see cref="JsonSerializer"/> directly (case-insensitive property matching) — a configured
    /// field map only applies to the dynamic (config-driven) path; the typed path expects JSON field
    /// names to already match <typeparamref name="T"/>'s property names.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IBatchSource<T> As<T>()
    {
        HttpClient client = _client ?? HttpClients.Default;
        T Materialize(JsonElement element) => element.Deserialize<T>(TypedDeserializeOptions)!;

        return new ElasticsearchBatchSource<T>(client, _url, _index, _options, Source.Placeholder, Materialize);
    }
}
