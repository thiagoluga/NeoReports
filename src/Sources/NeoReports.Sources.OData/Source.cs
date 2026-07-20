using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.OData;

/// <summary>Fluent entry point for an OData v4 source (ADR D62).</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring an OData v4 source. Defaults to <see cref="ODataPaginationStrategy.NextLink"/>
    /// (follows the response's <c>@odata.nextLink</c>) — call <see cref="ODataSourceBuilder.Paginate"/>
    /// to select the <see cref="ODataPaginationStrategy.Skip"/> strategy instead.
    /// </summary>
    /// <param name="resourceUrl">The OData resource's (collection's) URL.</param>
    /// <param name="client">An explicit <see cref="HttpClient"/> (caller owns its lifetime), or <c>null</c> to use a lazily-created shared instance.</param>
    public static ODataSourceBuilder OData(string resourceUrl, HttpClient? client = null) => new(resourceUrl, client);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for an OData v4 source, before the row type is chosen.</summary>
public sealed class ODataSourceBuilder
{
    private static readonly JsonSerializerOptions TypedDeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _resourceUrl;
    private readonly HttpClient? _client;
    private readonly ODataSourceOptions _options = new();

    internal ODataSourceBuilder(string resourceUrl, HttpClient? client)
    {
        _resourceUrl = string.IsNullOrWhiteSpace(resourceUrl) ? throw new ArgumentException("Resource URL must be provided.", nameof(resourceUrl)) : resourceUrl;
        _client = client;
    }

    /// <summary>Sets the pagination strategy.</summary>
    public ODataSourceBuilder Paginate(ODataPaginationStrategy strategy)
    {
        _options.Paginate(strategy);
        return this;
    }

    /// <summary>Sets the dotted path to the records array within the response body; defaults to <c>"value"</c>.</summary>
    public ODataSourceBuilder RecordsAt(string jsonPath)
    {
        _options.RecordsAt(jsonPath);
        return this;
    }

    /// <summary>Sets a static, author-supplied <c>$filter</c> expression, appended to every request as-is.</summary>
    public ODataSourceBuilder Filter(string odataFilterExpression)
    {
        _options.Filter(odataFilterExpression);
        return this;
    }

    /// <summary>Sets a static <c>$select</c> expression (comma-separated field names).</summary>
    public ODataSourceBuilder Select(string commaSeparatedFields)
    {
        _options.Select(commaSeparatedFields);
        return this;
    }

    /// <summary>Sets a static <c>$orderby</c> expression.</summary>
    public ODataSourceBuilder OrderBy(string expression)
    {
        _options.OrderBy(expression);
        return this;
    }

    /// <summary>
    /// Sets the explicit <c>$top</c> value used by <see cref="ODataPaginationStrategy.Skip"/>; when
    /// unset, <c>BatchContext.PageSize</c> is used instead. Meaningless for
    /// <see cref="ODataPaginationStrategy.NextLink"/>.
    /// </summary>
    public ODataSourceBuilder Top(int pageSize)
    {
        _options.Top(pageSize);
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public ODataSourceBuilder Header(string name, string value)
    {
        _options.Header(name, value);
        return this;
    }

    /// <summary>Sends a static API key as a request header (static auth only; OAuth2 is deferred, ADR D61/D62).</summary>
    public ODataSourceBuilder ApiKey(string headerName, string value)
    {
        _options.ApiKey(headerName, value);
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (static auth only; ADR D61/D62).</summary>
    public ODataSourceBuilder Bearer(string token)
    {
        _options.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the resource URL; defaults to the resource URL itself.</summary>
    public ODataSourceBuilder HealthCheckAt(string path)
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

        return new ODataBatchSource<T>(client, _resourceUrl, _options, Source.Placeholder, Materialize);
    }
}
