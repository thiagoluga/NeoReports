using System.Text.Json;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// Reads <see cref="GraphQlSourceOptions"/> and the endpoint URL from a dynamic-path source's
/// <c>properties</c> bag (ADR D63). Generic property-bag reads delegate to <see cref="PropertyBag"/>
/// (shared with the HTTP family via <c>Http.Common</c>); this type keeps only the GraphQL-specific
/// reads — the required <c>url</c>/<c>query</c>/<c>connectionPath</c>, and assembling
/// <see cref="GraphQlSourceOptions"/> from the rest.
/// </summary>
internal static class GraphQlConfigProperties
{
    /// <summary>Reads the required <c>url</c> property.</summary>
    public static string RequireUrl(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "url", "GraphQL");

    /// <summary>Reads the required <c>query</c> property (the GraphQL document).</summary>
    public static string RequireQuery(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "query", "GraphQL");

    /// <summary>
    /// Reads every <see cref="GraphQlSourceOptions"/> setting from the properties bag.
    /// <paramref name="requireQueryAndConnection"/> is <c>true</c> for a real read (the query
    /// document and connection path are mandatory to page a Relay connection) and <c>false</c> for
    /// <see cref="GraphQlSourceHealthCheck"/>, which only needs the URL and auth to probe the
    /// endpoint — requiring them there would make "test connection" fail on an incompletely
    /// configured source before the author has written a query yet, contradicting this source's own
    /// documented "does not validate the author's query/connection" honesty boundary (D36/D63).
    /// </summary>
    public static GraphQlSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties, bool requireQueryAndConnection = true)
    {
        var options = new GraphQlSourceOptions();
        if (requireQueryAndConnection)
        {
            options.Query(RequireQuery(properties));
            options.Connection(PropertyBag.RequireString(properties, "connectionPath", "GraphQL"));
        }

        if (properties is null)
            return options;

        if (PropertyBag.TryGetObject(properties, "variables", out JsonElement variablesElement))
            options.Variables(ReadVariables(variablesElement));

        if (PropertyBag.TryGetString(properties, "nodePath", out string? nodePath))
            options.Node(nodePath);

        bool hasFirst = PropertyBag.TryGetString(properties, "firstVariable", out string? firstVariable);
        bool hasAfter = PropertyBag.TryGetString(properties, "afterVariable", out string? afterVariable);
        if (hasFirst || hasAfter)
            options.PageVariables(hasFirst ? firstVariable! : "first", hasAfter ? afterVariable! : "after");

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

        return options;
    }

    /// <summary>
    /// Converts a nested JSON <c>variables</c> object into the <see cref="Dictionary{TKey,TValue}"/>
    /// shape <see cref="GraphQlSourceOptions.Variables"/> expects, preserving each property's actual
    /// JSON kind (string/number/bool/null) verbatim. Deliberately does NOT reuse
    /// <c>NeoReports.Core.Configuration.PrimitiveObjectConverter</c> — that converter opportunistically
    /// re-parses any date-looking string into a <see cref="DateTime"/> (the right call for a SQL
    /// bind-parameter value, its actual use case) and re-emits it in full round-trip ISO-8601 form,
    /// which would silently mangle a GraphQL variable an author configured as a plain string
    /// (e.g. <c>"2024-01-01"</c> becoming <c>"2024-01-01T00:00:00.0000000"</c> on the wire) — a real
    /// bug code review caught: many GraphQL servers validate a custom scalar strictly and reject the
    /// reformatted value. A variable's value must reach the server exactly as configured.
    /// </summary>
    private static Dictionary<string, object?> ReadVariables(JsonElement variablesElement)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in variablesElement.EnumerateObject())
            result[property.Name] = ConvertVariableValue(property.Value);

        return result;
    }

    private static object? ConvertVariableValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out long l) ? l : value.GetDouble(),
        _ => value.Clone(),
    };
}
