using System.Text.Json;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.HubSpot;

/// <summary>
/// Reads <see cref="HubSpotSourceOptions"/> and the required <c>objectType</c> from a dynamic-path
/// source's <c>properties</c> bag (ADR D65). Generic property-bag reads delegate to
/// <see cref="PropertyBag"/> (shared with the HTTP family via <c>Http.Common</c>); this type keeps
/// only the HubSpot-specific reads.
/// </summary>
internal static class HubSpotConfigProperties
{
    /// <summary>Reads the required <c>objectType</c> property (e.g. <c>"contacts"</c>).</summary>
    public static string RequireObjectType(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "objectType", "HubSpot");

    /// <summary>Reads every <see cref="HubSpotSourceOptions"/> setting from the properties bag; unset properties keep the option's default.</summary>
    public static HubSpotSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties)
    {
        var options = new HubSpotSourceOptions();
        if (properties is null)
            return options;

        if (PropertyBag.TryGetString(properties, "baseUrl", out string? baseUrl))
            options.BaseUrl(baseUrl);

        if (properties.TryGetValue("properties", out object? propertiesRaw) && propertiesRaw is JsonElement { ValueKind: JsonValueKind.Array } propertiesElement)
        {
            string[] names = propertiesElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
            if (names.Length > 0)
                options.Properties(names);
        }

        PropertyBag.ApplyCommonFieldsAndAuth(properties, options);

        return options;
    }
}
