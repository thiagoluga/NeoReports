using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// Reads <see cref="ElasticsearchSourceOptions"/> and the required <c>url</c>/<c>index</c> from a
/// dynamic-path source's <c>properties</c> bag (ADR D64). Generic property-bag reads delegate to
/// <see cref="PropertyBag"/> (shared with the HTTP family via <c>Http.Common</c>); this type keeps
/// only the Elasticsearch-specific reads.
/// </summary>
internal static class ElasticsearchConfigProperties
{
    /// <summary>Reads the required <c>url</c> property.</summary>
    public static string RequireUrl(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "url", "Elasticsearch");

    /// <summary>Reads the required <c>index</c> property.</summary>
    public static string RequireIndex(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "index", "Elasticsearch");

    /// <summary>
    /// Reads every <see cref="ElasticsearchSourceOptions"/> setting from the properties bag; unset
    /// properties keep the option's default. <paramref name="requireSort"/> is <c>false</c> for the
    /// health check only — "test connection" on a source whose author hasn't written its
    /// <c>sort</c> yet must not fail on that account (mirrors D63's GraphQL health-check fix).
    /// </summary>
    public static ElasticsearchSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties, bool requireSort = true)
    {
        var options = new ElasticsearchSourceOptions();

        if (properties is not null && properties.TryGetValue("sort", out object? sortRaw) && sortRaw is JsonElement { ValueKind: JsonValueKind.Array } sortElement)
            options.Sort(sortElement.GetRawText());
        else if (requireSort)
        {
            throw new ConfigurationException(
                "The Elasticsearch source requires a non-empty 'sort' property (a JSON array of Elasticsearch sort clauses) — search_after paging has no default.");
        }

        if (properties is null)
            return options;

        if (PropertyBag.TryGetObject(properties, "query", out JsonElement queryElement))
            options.Query(queryElement.GetRawText());

        if (PropertyBag.TryGetObject(properties, "fieldMap", out JsonElement fieldMapElement))
            options.FieldsFrom(PropertyBag.ToStringMap(fieldMapElement));

        if (PropertyBag.TryGetObject(properties, "headers", out JsonElement headersElement))
        {
            foreach (KeyValuePair<string, string> header in PropertyBag.ToStringMap(headersElement))
                options.Header(header.Key, header.Value);
        }

        if (PropertyBag.TryGetString(properties, "apiKeyHeader", out string? apiKeyHeader) && PropertyBag.TryGetString(properties, "apiKeyValue", out string? apiKeyValue))
            options.ApiKey(apiKeyHeader, apiKeyValue);

        if (PropertyBag.TryGetString(properties, "bearerToken", out string? bearerToken))
            options.Bearer(bearerToken);

        if (PropertyBag.TryGetString(properties, "healthCheckPath", out string? healthCheckPath))
            options.HealthCheckAt(healthCheckPath);

        return options;
    }
}
