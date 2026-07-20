using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Airtable;

/// <summary>
/// Fluent, mutable options for the Airtable source (ADR D65) — mirrors the shape of
/// <c>NeoReports.Sources.HubSpot.HubSpotSourceOptions</c>, but with no <c>properties</c>-selection
/// knob — unlike HubSpot, Airtable's response already includes every field the record has a value
/// for by default.
/// </summary>
public sealed class AirtableSourceOptions
{
    /// <summary>Airtable API host override, for the rare self-hosted-proxy/mocking case. Default <c>https://api.airtable.com/v0</c>.</summary>
    internal string BaseUrlValue { get; private set; } = "https://api.airtable.com/v0";

    /// <summary>
    /// Optional report-column-name to dotted-JSON-field-path map (relative to the <c>fields</c>
    /// envelope), for the dynamic (config-driven) path only.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? FieldMap { get; private set; }

    private readonly MutableHttpAuth _auth = new();

    /// <summary>Path probed by the health check, relative to the resolved table URL; when unset, that URL itself is probed.</summary>
    internal string? HealthCheckPath { get; private set; }

    /// <summary>Overrides the Airtable API host (advanced; default <c>https://api.airtable.com/v0</c>).</summary>
    public AirtableSourceOptions BaseUrl(string apiHost)
    {
        BaseUrlValue = string.IsNullOrWhiteSpace(apiHost) ? throw new ArgumentException("API host must be provided.", nameof(apiHost)) : apiHost;
        return this;
    }

    /// <summary>Maps report columns to dotted JSON field paths within the <c>fields</c> envelope (dynamic path only).</summary>
    public AirtableSourceOptions FieldsFrom(IReadOnlyDictionary<string, string> fieldMap)
    {
        FieldMap = fieldMap ?? throw new ArgumentNullException(nameof(fieldMap));
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public AirtableSourceOptions Header(string name, string value)
    {
        _auth.Header(name, value);
        return this;
    }

    /// <summary>Sends the Airtable personal access token (<c>Authorization: Bearer &lt;token&gt;</c>).</summary>
    public AirtableSourceOptions Bearer(string token)
    {
        _auth.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the resolved table URL; defaults to that URL itself.</summary>
    public AirtableSourceOptions HealthCheckAt(string path)
    {
        HealthCheckPath = path ?? throw new ArgumentNullException(nameof(path));
        return this;
    }

    /// <summary>Projects this instance's auth-related fields into the shared, source-agnostic <see cref="HttpAuth"/> shape.</summary>
    internal HttpAuth ToAuth() => _auth.ToAuth();
}
