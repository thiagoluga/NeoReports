using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>
/// Reads <see cref="SalesforceSourceOptions"/> and the required <c>instanceUrl</c>/<c>soql</c> from
/// a dynamic-path source's <c>properties</c> bag (ADR D67). Generic property-bag reads delegate to
/// <see cref="PropertyBag"/> (shared with the HTTP family via <c>Http.Common</c>); this type keeps
/// only the Salesforce-specific reads.
/// </summary>
internal static class SalesforceConfigProperties
{
    /// <summary>Reads the required <c>instanceUrl</c> property (e.g. <c>"https://myorg.my.salesforce.com"</c>).</summary>
    public static string RequireInstanceUrl(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "instanceUrl", "Salesforce");

    /// <summary>Reads the required <c>soql</c> property.</summary>
    public static string RequireSoql(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "soql", "Salesforce");

    /// <summary>Reads every <see cref="SalesforceSourceOptions"/> setting from the properties bag; unset properties keep the option's default.</summary>
    public static SalesforceSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties)
    {
        var options = new SalesforceSourceOptions();
        options.Bearer(PropertyBag.RequireString(properties, "bearerToken", "Salesforce"));

        if (properties is null)
            return options;

        if (PropertyBag.TryGetString(properties, "apiVersion", out string? apiVersion))
            options.ApiVersion(apiVersion);

        PropertyBag.ApplyCommonFieldsAndAuth(properties, options);

        return options;
    }
}
