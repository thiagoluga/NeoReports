using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.HubSpot;

/// <summary>Fluent entry point for a HubSpot CRM source (ADR D65).</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>Begins configuring a HubSpot CRM object-collection source (e.g. <c>"contacts"</c>, <c>"companies"</c>, <c>"deals"</c>).</summary>
    /// <param name="objectType">The CRM object type to read.</param>
    /// <param name="token">The HubSpot private-app access token.</param>
    /// <param name="client">An explicit <see cref="HttpClient"/> (caller owns its lifetime), or <c>null</c> to use a lazily-created shared instance.</param>
    public static HubSpotSourceBuilder HubSpot(string objectType, string token, HttpClient? client = null) =>
        new HubSpotSourceBuilder(objectType, client).Bearer(token);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for a HubSpot CRM source, before the row type is chosen.</summary>
public sealed class HubSpotSourceBuilder
{
    private static readonly JsonSerializerOptions TypedDeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _objectType;
    private readonly HttpClient? _client;
    private readonly HubSpotSourceOptions _options = new();

    internal HubSpotSourceBuilder(string objectType, HttpClient? client)
    {
        _objectType = string.IsNullOrWhiteSpace(objectType) ? throw new ArgumentException("Object type must be provided.", nameof(objectType)) : objectType;
        _client = client;
    }

    /// <summary>Overrides the HubSpot API host (advanced; default <c>https://api.hubapi.com</c>).</summary>
    public HubSpotSourceBuilder BaseUrl(string apiHost)
    {
        _options.BaseUrl(apiHost);
        return this;
    }

    /// <summary>Sets the CRM property names to request via <c>?properties=</c>.</summary>
    public HubSpotSourceBuilder Properties(params string[] propertyNames)
    {
        _options.Properties(propertyNames);
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public HubSpotSourceBuilder Header(string name, string value)
    {
        _options.Header(name, value);
        return this;
    }

    /// <summary>Sends the HubSpot private-app access token (<c>Authorization: Bearer &lt;token&gt;</c>).</summary>
    public HubSpotSourceBuilder Bearer(string token)
    {
        _options.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the resolved object-collection URL; defaults to that URL itself.</summary>
    public HubSpotSourceBuilder HealthCheckAt(string path)
    {
        _options.HealthCheckAt(path);
        return this;
    }

    /// <summary>
    /// Completes the source, materializing each result's <c>properties</c> envelope as
    /// <typeparamref name="T"/> via <see cref="JsonSerializer"/> directly (case-insensitive property
    /// matching) — a configured field map only applies to the dynamic (config-driven) path; the typed
    /// path expects HubSpot property names to already match <typeparamref name="T"/>'s property names.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IBatchSource<T> As<T>()
    {
        HttpClient client = _client ?? HttpClients.Default;
        T Materialize(JsonElement element) => element.Deserialize<T>(TypedDeserializeOptions)!;

        return new HubSpotBatchSource<T>(client, _objectType, _options, Source.Placeholder, Materialize);
    }
}
