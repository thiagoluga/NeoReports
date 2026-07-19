using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.OData;

/// <summary>
/// Reads <see cref="ODataSourceOptions"/> and the resource URL from a dynamic-path source's
/// <c>properties</c> bag (ADR D62). Generic property-bag reads delegate to
/// <see cref="PropertyBag"/> (shared with the HTTP family via <c>Http.Common</c>); this type keeps
/// only the OData-specific reads (the required <c>url</c>, and assembling
/// <see cref="ODataSourceOptions"/> from the rest).
/// </summary>
internal static class ODataConfigProperties
{
    /// <summary>Reads the required <c>url</c> property.</summary>
    public static string RequireUrl(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "url", "OData");

    /// <summary>Reads every <see cref="ODataSourceOptions"/> setting from the properties bag; unset properties keep the option's default.</summary>
    public static ODataSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties)
    {
        var options = new ODataSourceOptions();
        if (properties is null)
            return options;

        if (PropertyBag.TryGetString(properties, "strategy", out string? strategyText))
        {
            if (!Enum.TryParse(strategyText, ignoreCase: true, out ODataPaginationStrategy strategy))
            {
                throw new ConfigurationException(
                    $"The OData source's 'strategy' property '{strategyText}' is not a recognized pagination strategy " +
                    $"(expected one of: {string.Join(", ", Enum.GetNames<ODataPaginationStrategy>())}).");
            }

            options.Paginate(strategy);
        }

        if (PropertyBag.TryGetString(properties, "recordsPath", out string? recordsPath))
            options.RecordsAt(recordsPath);

        if (PropertyBag.TryGetString(properties, "filter", out string? filter))
            options.Filter(filter);

        if (PropertyBag.TryGetString(properties, "select", out string? select))
            options.Select(select);

        if (PropertyBag.TryGetString(properties, "orderby", out string? orderBy))
            options.OrderBy(orderBy);

        if (PropertyBag.TryGetInt(properties, "top", out int top))
            options.Top(top);

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
