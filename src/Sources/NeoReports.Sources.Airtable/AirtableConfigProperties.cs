using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Airtable;

/// <summary>
/// Reads <see cref="AirtableSourceOptions"/> and the required <c>baseId</c>/<c>table</c> from a
/// dynamic-path source's <c>properties</c> bag (ADR D65). Generic property-bag reads delegate to
/// <see cref="PropertyBag"/> (shared with the HTTP family via <c>Http.Common</c>); this type keeps
/// only the Airtable-specific reads.
/// </summary>
internal static class AirtableConfigProperties
{
    /// <summary>Reads the required <c>baseId</c> property.</summary>
    public static string RequireBaseId(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "baseId", "Airtable");

    /// <summary>Reads the required <c>table</c> property (table id or name).</summary>
    public static string RequireTable(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "table", "Airtable");

    /// <summary>Reads every <see cref="AirtableSourceOptions"/> setting from the properties bag; unset properties keep the option's default.</summary>
    public static AirtableSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties)
    {
        var options = new AirtableSourceOptions();
        if (properties is null)
            return options;

        if (PropertyBag.TryGetString(properties, "baseUrl", out string? baseUrl))
            options.BaseUrl(baseUrl);

        PropertyBag.ApplyCommonFieldsAndAuth(properties, options);

        return options;
    }
}
