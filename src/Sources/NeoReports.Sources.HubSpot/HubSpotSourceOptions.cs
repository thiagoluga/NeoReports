using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.HubSpot;

/// <summary>
/// Fluent, mutable options for the HubSpot source (ADR D65) — mirrors the shape of
/// <c>NeoReports.Sources.OData.ODataSourceOptions</c>, but with no pagination-strategy choice
/// (HubSpot has exactly one paging mechanism).
/// </summary>
public sealed class HubSpotSourceOptions
{
    /// <summary>HubSpot API host override, for the rare self-hosted-proxy/mocking case. Default <c>https://api.hubapi.com</c>.</summary>
    internal string BaseUrlValue { get; private set; } = "https://api.hubapi.com";

    /// <summary>
    /// Property names requested via <c>?properties=</c>. Not optional in practice — HubSpot's
    /// default response for most object types includes only a handful of standard fields; a
    /// property not requested here is silently absent from the response (materializes as
    /// <c>null</c> rather than erroring, per <c>JsonRecordMaterializer</c>'s "missing field" contract).
    /// </summary>
    internal IReadOnlyList<string>? RequestedProperties { get; private set; }

    /// <summary>
    /// Optional report-column-name to dotted-JSON-field-path map (relative to the <c>properties</c>
    /// envelope), for the dynamic (config-driven) path only.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? FieldMap { get; private set; }

    private readonly MutableHttpAuth _auth = new();

    /// <summary>Path probed by the health check, relative to the resolved object-collection URL; when unset, that URL itself is probed.</summary>
    internal string? HealthCheckPath { get; private set; }

    /// <summary>Overrides the HubSpot API host (advanced; default <c>https://api.hubapi.com</c>).</summary>
    public HubSpotSourceOptions BaseUrl(string apiHost)
    {
        BaseUrlValue = string.IsNullOrWhiteSpace(apiHost) ? throw new ArgumentException("API host must be provided.", nameof(apiHost)) : apiHost;
        return this;
    }

    /// <summary>Sets the CRM property names to request via <c>?properties=</c>.</summary>
    public HubSpotSourceOptions Properties(params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);
        RequestedProperties = propertyNames.Length == 0 ? null : propertyNames;
        return this;
    }

    /// <summary>Maps report columns to dotted JSON field paths within the <c>properties</c> envelope (dynamic path only).</summary>
    public HubSpotSourceOptions FieldsFrom(IReadOnlyDictionary<string, string> fieldMap)
    {
        FieldMap = fieldMap ?? throw new ArgumentNullException(nameof(fieldMap));
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public HubSpotSourceOptions Header(string name, string value)
    {
        _auth.Header(name, value);
        return this;
    }

    /// <summary>Sends the HubSpot private-app access token (<c>Authorization: Bearer &lt;token&gt;</c>).</summary>
    public HubSpotSourceOptions Bearer(string token)
    {
        _auth.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the resolved object-collection URL; defaults to that URL itself.</summary>
    public HubSpotSourceOptions HealthCheckAt(string path)
    {
        HealthCheckPath = path ?? throw new ArgumentNullException(nameof(path));
        return this;
    }

    /// <summary>Projects this instance's auth-related fields into the shared, source-agnostic <see cref="HttpAuth"/> shape.</summary>
    internal HttpAuth ToAuth() => _auth.ToAuth();
}
