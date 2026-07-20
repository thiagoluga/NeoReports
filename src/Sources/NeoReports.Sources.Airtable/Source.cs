using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Airtable;

/// <summary>Fluent entry point for an Airtable source (ADR D65).</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>Begins configuring an Airtable table source.</summary>
    /// <param name="baseId">The Airtable base id (e.g. <c>"appXXXXXXXXXXXXXX"</c>).</param>
    /// <param name="tableIdOrName">The table id or name within the base.</param>
    /// <param name="token">The Airtable personal access token.</param>
    /// <param name="client">An explicit <see cref="HttpClient"/> (caller owns its lifetime), or <c>null</c> to use a lazily-created shared instance.</param>
    public static AirtableSourceBuilder Airtable(string baseId, string tableIdOrName, string token, HttpClient? client = null) =>
        new AirtableSourceBuilder(baseId, tableIdOrName, client).Bearer(token);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for an Airtable source, before the row type is chosen.</summary>
public sealed class AirtableSourceBuilder
{
    private static readonly JsonSerializerOptions TypedDeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _baseId;
    private readonly string _tableIdOrName;
    private readonly HttpClient? _client;
    private readonly AirtableSourceOptions _options = new();

    internal AirtableSourceBuilder(string baseId, string tableIdOrName, HttpClient? client)
    {
        _baseId = string.IsNullOrWhiteSpace(baseId) ? throw new ArgumentException("Base id must be provided.", nameof(baseId)) : baseId;
        _tableIdOrName = string.IsNullOrWhiteSpace(tableIdOrName) ? throw new ArgumentException("Table id/name must be provided.", nameof(tableIdOrName)) : tableIdOrName;
        _client = client;
    }

    /// <summary>Overrides the Airtable API host (advanced; default <c>https://api.airtable.com/v0</c>).</summary>
    public AirtableSourceBuilder BaseUrl(string apiHost)
    {
        _options.BaseUrl(apiHost);
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public AirtableSourceBuilder Header(string name, string value)
    {
        _options.Header(name, value);
        return this;
    }

    /// <summary>Sends the Airtable personal access token (<c>Authorization: Bearer &lt;token&gt;</c>).</summary>
    public AirtableSourceBuilder Bearer(string token)
    {
        _options.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the resolved table URL; defaults to that URL itself.</summary>
    public AirtableSourceBuilder HealthCheckAt(string path)
    {
        _options.HealthCheckAt(path);
        return this;
    }

    /// <summary>
    /// Completes the source, materializing each record's <c>fields</c> envelope as
    /// <typeparamref name="T"/> via <see cref="JsonSerializer"/> directly (case-insensitive property
    /// matching) — a configured field map only applies to the dynamic (config-driven) path; the typed
    /// path expects Airtable field names to already match <typeparamref name="T"/>'s property names.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IBatchSource<T> As<T>()
    {
        HttpClient client = _client ?? HttpClients.Default;
        T Materialize(JsonElement element) => element.Deserialize<T>(TypedDeserializeOptions)!;

        return new AirtableBatchSource<T>(client, _baseId, _tableIdOrName, _options, Source.Placeholder, Materialize);
    }
}
