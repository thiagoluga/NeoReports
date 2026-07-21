using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>
/// Fluent, mutable options for the Salesforce source (ADR D67). Implements
/// <see cref="ICommonHttpOptions{TSelf}"/> so <see cref="PropertyBag.ApplyCommonFieldsAndAuth{TOptions}"/>
/// can apply the shared dynamic-path properties without <c>SalesforceConfigProperties</c> keeping
/// its own copy of that block.
/// </summary>
public sealed class SalesforceSourceOptions : ICommonHttpOptions<SalesforceSourceOptions>
{
    /// <summary>REST API version. Default <c>v59.0</c>.</summary>
    internal string ApiVersionValue { get; private set; } = "v59.0";

    /// <summary>
    /// Optional report-column-name to dotted-JSON-field-path map, for the dynamic (config-driven)
    /// path only — a typed <c>.As&lt;T&gt;()</c> read always matches JSON fields to <c>T</c>'s
    /// properties directly by name.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? FieldMap { get; private set; }

    private readonly MutableHttpAuth _auth = new();

    /// <summary>Path probed by the health check, relative to the resolved "list resources" URL; when unset, that URL itself is probed.</summary>
    internal string? HealthCheckPath { get; private set; }

    /// <summary>Overrides the REST API version (default <c>v59.0</c>).</summary>
    public SalesforceSourceOptions ApiVersion(string version)
    {
        ApiVersionValue = string.IsNullOrWhiteSpace(version) ? throw new ArgumentException("API version must be provided.", nameof(version)) : version;
        return this;
    }

    /// <inheritdoc />
    public SalesforceSourceOptions FieldsFrom(IReadOnlyDictionary<string, string> fieldMap)
    {
        FieldMap = fieldMap ?? throw new ArgumentNullException(nameof(fieldMap));
        return this;
    }

    /// <inheritdoc />
    public SalesforceSourceOptions Header(string name, string value)
    {
        _auth.Header(name, value);
        return this;
    }

    /// <summary>Sends the Salesforce access token (<c>Authorization: Bearer &lt;token&gt;</c>).</summary>
    public SalesforceSourceOptions Bearer(string token)
    {
        _auth.Bearer(token);
        return this;
    }

    /// <inheritdoc />
    public SalesforceSourceOptions HealthCheckAt(string path)
    {
        HealthCheckPath = path ?? throw new ArgumentNullException(nameof(path));
        return this;
    }

    /// <summary>Projects this instance's auth-related fields into the shared, source-agnostic <see cref="HttpAuth"/> shape.</summary>
    internal HttpAuth ToAuth() => _auth.ToAuth();
}
