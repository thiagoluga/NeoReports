using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Http;

/// <summary>
/// Reads <see cref="HttpSourceOptions"/> and the base URL from a dynamic-path source's
/// <c>properties</c> bag (ADR D61). Nested JSON objects (<c>fieldMap</c>, <c>headers</c>) arrive as
/// a cloned <see cref="JsonElement"/> (<c>PrimitiveObjectConverter</c>'s documented shape for
/// property-bag values), not a CLR dictionary — read accordingly.
/// </summary>
internal static class HttpConfigProperties
{
    /// <summary>Reads the required <c>url</c> property.</summary>
    public static string RequireUrl(IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is not null && properties.TryGetValue("url", out object? value)
            && value is string url && !string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        throw new ConfigurationException("The HTTP source requires a non-empty 'url' property.");
    }

    /// <summary>Reads every <see cref="HttpSourceOptions"/> setting from the properties bag; unset properties keep the option's default.</summary>
    public static HttpSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties)
    {
        var options = new HttpSourceOptions();
        if (properties is null)
            return options;

        if (TryGetString(properties, "strategy", out string? strategyText))
        {
            if (!Enum.TryParse(strategyText, ignoreCase: true, out HttpPaginationStrategy strategy))
            {
                throw new ConfigurationException(
                    $"The HTTP source's 'strategy' property '{strategyText}' is not a recognized pagination strategy " +
                    $"(expected one of: {string.Join(", ", Enum.GetNames<HttpPaginationStrategy>())}).");
            }

            options.Paginate(strategy);
        }

        if (TryGetString(properties, "recordsPath", out string? recordsPath))
            options.RecordsAt(recordsPath);

        if (TryGetObject(properties, "fieldMap", out JsonElement fieldMapElement))
            options.FieldsFrom(ToStringMap(fieldMapElement));

        string? cursorResponsePath = TryGetString(properties, "cursorResponsePath", out string? crp) ? crp : null;
        string? cursorRequestParam = TryGetString(properties, "cursorRequestParam", out string? crq) ? crq : null;
        if (cursorResponsePath is not null || cursorRequestParam is not null)
            options.CursorField(cursorResponsePath ?? "nextCursor", cursorRequestParam ?? "cursor");

        string? pageParam = TryGetString(properties, "pageParam", out string? pp) ? pp : null;
        string? pageSizeParam = TryGetString(properties, "pageSizeParam", out string? psp) ? psp : null;
        int? startPage = TryGetInt(properties, "startPage", out int sp) ? sp : null;
        if (pageParam is not null || pageSizeParam is not null || startPage is not null)
            options.PageParams(pageParam ?? "page", pageSizeParam ?? "pageSize", startPage ?? 1);

        string? offsetParam = TryGetString(properties, "offsetParam", out string? op) ? op : null;
        string? limitParam = TryGetString(properties, "limitParam", out string? lp) ? lp : null;
        if (offsetParam is not null || limitParam is not null)
            options.OffsetParams(offsetParam ?? "offset", limitParam ?? "pageSize");

        if (TryGetObject(properties, "headers", out JsonElement headersElement))
        {
            foreach (KeyValuePair<string, string> header in ToStringMap(headersElement))
                options.Header(header.Key, header.Value);
        }

        if (TryGetString(properties, "apiKeyHeader", out string? apiKeyHeader) && TryGetString(properties, "apiKeyValue", out string? apiKeyValue))
            options.ApiKey(apiKeyHeader, apiKeyValue);

        if (TryGetString(properties, "bearerToken", out string? bearerToken))
            options.Bearer(bearerToken);

        if (TryGetString(properties, "healthCheckPath", out string? healthCheckPath))
            options.HealthCheckAt(healthCheckPath);

        return options;
    }

    private static bool TryGetString(IReadOnlyDictionary<string, object?> properties, string key, [NotNullWhen(true)] out string? value)
    {
        if (properties.TryGetValue(key, out object? raw) && raw is string { Length: > 0 } text)
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, object?> properties, string key, out int value)
    {
        if (properties.TryGetValue(key, out object? raw))
        {
            switch (raw)
            {
                case long l:
                    value = checked((int)l);
                    return true;
                case double d:
                    value = (int)d;
                    return true;
                case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                    value = parsed;
                    return true;
                case JsonElement { ValueKind: JsonValueKind.Number } e:
                    value = e.GetInt32();
                    return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetObject(IReadOnlyDictionary<string, object?> properties, string key, out JsonElement value)
    {
        if (properties.TryGetValue(key, out object? raw) && raw is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            value = element;
            return true;
        }

        value = default;
        return false;
    }

    private static Dictionary<string, string> ToStringMap(JsonElement obj) =>
        obj.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
}
