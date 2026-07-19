using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GraphQl;

/// <summary>Fluent entry point for a GraphQL source over a Relay connection (ADR D63).</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring a GraphQL source. The query document (<see cref="GraphQlSourceBuilder.Query"/>)
    /// and the Relay connection's dotted path (<see cref="GraphQlSourceBuilder.Connection"/>) must both
    /// be set before <see cref="GraphQlSourceBuilder.As{T}"/> — every GraphQL schema is different, so
    /// there is nothing this source can synthesize on its own.
    /// </summary>
    /// <param name="endpointUrl">The GraphQL endpoint's URL (single endpoint, every query is <c>POST</c>ed there).</param>
    /// <param name="client">An explicit <see cref="HttpClient"/> (caller owns its lifetime), or <c>null</c> to use a lazily-created shared instance.</param>
    public static GraphQlSourceBuilder GraphQl(string endpointUrl, HttpClient? client = null) => new(endpointUrl, client);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for a GraphQL source, before the row type is chosen.</summary>
public sealed class GraphQlSourceBuilder
{
    private static readonly JsonSerializerOptions TypedDeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _endpointUrl;
    private readonly HttpClient? _client;
    private readonly GraphQlSourceOptions _options = new();

    internal GraphQlSourceBuilder(string endpointUrl, HttpClient? client)
    {
        _endpointUrl = string.IsNullOrWhiteSpace(endpointUrl) ? throw new ArgumentException("Endpoint URL must be provided.", nameof(endpointUrl)) : endpointUrl;
        _client = client;
    }

    /// <summary>Sets the GraphQL query document. Must declare the configured paging variables and select <c>pageInfo { hasNextPage endCursor }</c> on the configured connection.</summary>
    public GraphQlSourceBuilder Query(string document)
    {
        _options.Query(document);
        return this;
    }

    /// <summary>Sets static, author-supplied variables merged into every request alongside the paging variables.</summary>
    public GraphQlSourceBuilder Variables(IReadOnlyDictionary<string, object?> variables)
    {
        _options.Variables(variables);
        return this;
    }

    /// <summary>Sets the dotted path to the Relay connection within the response's <c>data</c> object (e.g. <c>"viewer.repositories"</c>).</summary>
    public GraphQlSourceBuilder Connection(string dottedPath)
    {
        _options.Connection(dottedPath);
        return this;
    }

    /// <summary>Sets the field name of each edge's node object; defaults to <c>"node"</c>.</summary>
    public GraphQlSourceBuilder Node(string fieldName)
    {
        _options.Node(fieldName);
        return this;
    }

    /// <summary>Sets the paging variable names, for a schema that names them differently than <c>first</c>/<c>after</c>.</summary>
    public GraphQlSourceBuilder PageVariables(string first, string after)
    {
        _options.PageVariables(first, after);
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public GraphQlSourceBuilder Header(string name, string value)
    {
        _options.Header(name, value);
        return this;
    }

    /// <summary>Sends a static API key as a request header (static auth only; OAuth2 is out of scope, ADR D61/D63).</summary>
    public GraphQlSourceBuilder ApiKey(string headerName, string value)
    {
        _options.ApiKey(headerName, value);
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (static auth only; ADR D61/D63).</summary>
    public GraphQlSourceBuilder Bearer(string token)
    {
        _options.Bearer(token);
        return this;
    }

    /// <summary>
    /// Completes the source, materializing each Relay edge's node as <typeparamref name="T"/> via
    /// <see cref="JsonSerializer"/> directly (case-insensitive property matching) — a configured
    /// field map only applies to the dynamic (config-driven) path; the typed path expects JSON
    /// field names to already match <typeparamref name="T"/>'s property names.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IBatchSource<T> As<T>()
    {
        HttpClient client = _client ?? HttpClients.Default;
        T Materialize(JsonElement element) => element.Deserialize<T>(TypedDeserializeOptions)!;

        return new GraphQlBatchSource<T>(client, _endpointUrl, _options, Source.Placeholder, Materialize);
    }
}
