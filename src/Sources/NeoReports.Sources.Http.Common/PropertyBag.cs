using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Generic readers for a dynamic-path source's <c>properties</c> bag (ADR D61). Nested JSON
/// objects arrive as a cloned <see cref="JsonElement"/> (<c>PrimitiveObjectConverter</c>'s
/// documented shape for property-bag values), not a CLR dictionary — read accordingly. Shared
/// across HTTP-family source packages so each one doesn't keep its own copy of this parsing.
/// </summary>
public static class PropertyBag
{
    /// <summary>
    /// Reads a required string property, or throws <see cref="ConfigurationException"/> naming
    /// <paramref name="sourceLabel"/> (e.g. <c>"HTTP"</c>, <c>"OData"</c>) when it's absent or
    /// whitespace-only — the shared shape behind every HTTP-family source's <c>RequireUrl</c>.
    /// Deliberately stricter than <see cref="TryGetString"/> (which only rejects an empty string):
    /// a required value like a source's URL must not be silently whitespace either.
    /// </summary>
    public static string RequireString(IReadOnlyDictionary<string, object?>? properties, string key, string sourceLabel)
    {
        if (properties is not null && TryGetString(properties, key, out string? value) && !string.IsNullOrWhiteSpace(value))
            return value;

        throw new ConfigurationException($"The {sourceLabel} source requires a non-empty '{key}' property.");
    }

    /// <summary>Reads a non-empty string property.</summary>
    public static bool TryGetString(IReadOnlyDictionary<string, object?> properties, string key, [NotNullWhen(true)] out string? value)
    {
        if (properties.TryGetValue(key, out object? raw) && raw is string { Length: > 0 } text)
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Reads an integer property, accepting a long/double/numeric-string/<see cref="JsonElement"/> number.</summary>
    public static bool TryGetInt(IReadOnlyDictionary<string, object?> properties, string key, out int value)
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

    /// <summary>Reads a nested JSON object property.</summary>
    public static bool TryGetObject(IReadOnlyDictionary<string, object?> properties, string key, out JsonElement value)
    {
        if (properties.TryGetValue(key, out object? raw) && raw is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            value = element;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Flattens a JSON object's string-valued properties into a <see cref="Dictionary{TKey,TValue}"/>.</summary>
    public static Dictionary<string, string> ToStringMap(JsonElement obj) =>
        obj.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
}
