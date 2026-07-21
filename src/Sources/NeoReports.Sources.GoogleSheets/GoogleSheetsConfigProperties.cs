using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// Reads <see cref="GoogleSheetsSourceOptions"/> and the required <c>spreadsheetId</c>/<c>sheet</c>
/// from a dynamic-path source's <c>properties</c> bag (ADR D66). Generic property-bag reads delegate
/// to <see cref="PropertyBag"/> (shared with the HTTP family via <c>Http.Common</c>); this type keeps
/// only the Google Sheets-specific reads.
/// </summary>
internal static class GoogleSheetsConfigProperties
{
    /// <summary>Reads the required <c>spreadsheetId</c> property.</summary>
    public static string RequireSpreadsheetId(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "spreadsheetId", "Google Sheets");

    /// <summary>Reads the required <c>sheet</c> property (the sheet/tab name).</summary>
    public static string RequireSheet(IReadOnlyDictionary<string, object?>? properties) =>
        PropertyBag.RequireString(properties, "sheet", "Google Sheets");

    /// <summary>
    /// Reads every <see cref="GoogleSheetsSourceOptions"/> setting from the properties bag.
    /// <paramref name="requireColumns"/> is <c>false</c> for the health check only — "test connection"
    /// on a source whose author hasn't configured <c>firstColumn</c>/<c>lastColumn</c> yet must not
    /// fail on that account (mirrors D63's GraphQL health-check fix); the API key is always required,
    /// since even a probe call needs it to authenticate.
    /// </summary>
    public static GoogleSheetsSourceOptions ReadOptions(IReadOnlyDictionary<string, object?>? properties, bool requireColumns = true)
    {
        var options = new GoogleSheetsSourceOptions().ApiKey(PropertyBag.RequireString(properties, "apiKey", "Google Sheets"));

        if (properties is not null && PropertyBag.TryGetInt(properties, "headerRow", out int headerRow))
            options.HeaderRow(headerRow);

        if (properties is not null
            && PropertyBag.TryGetString(properties, "firstColumn", out string? firstColumn)
            && PropertyBag.TryGetString(properties, "lastColumn", out string? lastColumn))
        {
            options.Columns(firstColumn, lastColumn);
        }
        else if (requireColumns)
        {
            throw new ConfigurationException("The Google Sheets source requires non-empty 'firstColumn'/'lastColumn' properties.");
        }

        return options;
    }
}
