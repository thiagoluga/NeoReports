using System.Globalization;
using System.Text.Json;

namespace NeoReports.Jobs;

/// <summary>
/// Serializes report run-time parameters to/from a JSON string so they can be persisted as job
/// arguments (e.g. by Hangfire). Round-trips primitive values (string, integer, floating point,
/// boolean) and ISO-8601 date/times; complex objects are out of scope for v1 parameters.
/// </summary>
public static class JobParameters
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Serializes a parameter dictionary to a compact JSON object string.</summary>
    /// <param name="parameters">The parameters; <c>null</c> serializes to an empty object.</param>
    public static string Serialize(IReadOnlyDictionary<string, object?>? parameters) =>
        JsonSerializer.Serialize(parameters ?? new Dictionary<string, object?>(), Options);

    /// <summary>
    /// Deserializes a JSON object string back into a parameter dictionary, converting each JSON
    /// value to the closest CLR primitive so downstream consumers (e.g. SQL parameters) receive
    /// usable values rather than raw <see cref="JsonElement"/>s.
    /// </summary>
    /// <param name="json">The JSON produced by <see cref="Serialize"/>.</param>
    public static IReadOnlyDictionary<string, object?> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>();

        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
            result[property.Name] = ConvertValue(property.Value);

        return result;
    }

    private static object? ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => ConvertString(value.GetString()),
        // The (object) cast is load-bearing — without it the conditional's static type unifies long
        // and double (long widens implicitly), silently re-boxing every whole number as a double even
        // when TryGetInt64 succeeded. That made this path disagree with the sync/in-memory one, which
        // yields long, and it loses precision past 2^53: a bigint id bound as a float compares wrong.
        // Same trap, same fix as PrimitiveObjectConverter.
        JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
        _ => value.GetRawText(),
    };

    private static object? ConvertString(string? text)
    {
        if (text is null)
            return null;

        // Recover round-tripped ISO-8601 timestamps as DateTime so SQL date parameters bind
        // correctly. RoundtripKind honors the 'Z'/offset in the text and preserves the kind; it
        // must NOT be combined with AdjustToUniversal/AssumeUniversal (.NET rejects that pairing).
        if (DateTime.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt;

        return text;
    }
}
