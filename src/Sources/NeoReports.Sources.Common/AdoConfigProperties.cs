using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Common;

/// <summary>
/// Shared property-bag readers for <c>IConfigSourceProvider</c> implementations across the
/// relational provider packages (Postgres, MySQL, Oracle) — the same parsing
/// <c>SqlConfigSourceProvider</c> does inline, extracted so it isn't triplicated.
/// </summary>
public static class AdoConfigProperties
{
    /// <summary>Reads a required, non-empty string property, or throws <see cref="ConfigurationException"/>.</summary>
    public static string RequireString(IReadOnlyDictionary<string, object?>? properties, string key, string sourceTypeLabel)
    {
        if (properties is not null
            && properties.TryGetValue(key, out var value)
            && value is string text
            && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new ConfigurationException($"The {sourceTypeLabel} source requires a non-empty '{key}' property.");
    }

    /// <summary>Reads an optional integer property (accepting numeric JSON, strings, or CLR numeric types).</summary>
    public static int? OptionalInt(IReadOnlyDictionary<string, object?>? properties, string key, string sourceTypeLabel)
    {
        if (properties is null || !properties.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            double d => (int)d,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetInt32(),
            _ => throw new ConfigurationException(
                $"The {sourceTypeLabel} source property '{key}' must be an integer (was {value.GetType().Name})."),
        };
    }
}
