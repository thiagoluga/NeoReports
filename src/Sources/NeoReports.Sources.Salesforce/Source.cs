using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>Fluent entry point for a Salesforce source (ADR D67).</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring a Salesforce source. Static access-token auth only — obtain and refresh
    /// the token externally (e.g. via Salesforce's JWT bearer flow); no built-in OAuth2, deferred
    /// alongside P4b (see D67).
    /// </summary>
    /// <param name="instanceUrl">The Salesforce org's instance URL (e.g. <c>https://myorg.my.salesforce.com</c>).</param>
    /// <param name="soql">The SOQL query to run.</param>
    /// <param name="accessToken">The Salesforce access token.</param>
    /// <param name="client">An explicit <see cref="HttpClient"/> (caller owns its lifetime), or <c>null</c> to use a lazily-created shared instance.</param>
    public static SalesforceSourceBuilder Salesforce(string instanceUrl, string soql, string accessToken, HttpClient? client = null) =>
        new SalesforceSourceBuilder(instanceUrl, soql, client).Bearer(accessToken);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for a Salesforce source, before the row type is chosen.</summary>
public sealed class SalesforceSourceBuilder
{
    private static readonly JsonSerializerOptions TypedDeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _instanceUrl;
    private readonly string _soql;
    private readonly HttpClient? _client;
    private readonly SalesforceSourceOptions _options = new();

    internal SalesforceSourceBuilder(string instanceUrl, string soql, HttpClient? client)
    {
        _instanceUrl = string.IsNullOrWhiteSpace(instanceUrl) ? throw new ArgumentException("Instance URL must be provided.", nameof(instanceUrl)) : instanceUrl;
        _soql = string.IsNullOrWhiteSpace(soql) ? throw new ArgumentException("SOQL query must be provided.", nameof(soql)) : soql;
        _client = client;
    }

    /// <summary>Overrides the REST API version (default <c>v59.0</c>).</summary>
    public SalesforceSourceBuilder ApiVersion(string version)
    {
        _options.ApiVersion(version);
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public SalesforceSourceBuilder Header(string name, string value)
    {
        _options.Header(name, value);
        return this;
    }

    /// <summary>Sends the Salesforce access token (<c>Authorization: Bearer &lt;token&gt;</c>).</summary>
    public SalesforceSourceBuilder Bearer(string token)
    {
        _options.Bearer(token);
        return this;
    }

    /// <summary>Sets the path the health check probes, relative to the resolved "list resources" URL; defaults to that URL itself.</summary>
    public SalesforceSourceBuilder HealthCheckAt(string path)
    {
        _options.HealthCheckAt(path);
        return this;
    }

    /// <summary>
    /// Completes the source, materializing each record directly as <typeparamref name="T"/> via
    /// <see cref="JsonSerializer"/> (case-insensitive property matching) — a configured field map
    /// only applies to the dynamic (config-driven) path.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IBatchSource<T> As<T>()
    {
        HttpClient client = _client ?? HttpClients.Default;
        T Materialize(JsonElement element) => element.Deserialize<T>(TypedDeserializeOptions)!;

        return new SalesforceBatchSource<T>(client, _instanceUrl, _soql, _options, Source.Placeholder, Materialize);
    }
}
