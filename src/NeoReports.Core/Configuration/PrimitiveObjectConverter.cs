using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Reads and writes JSON values typed as <c>object?</c> (property-bag values) as CLR primitives
/// (string, long, double, bool, ISO-8601 <see cref="DateTime"/>) or a cloned <see cref="JsonElement"/>
/// for nested objects/arrays — the shared property-bag shape used by <c>ReportConfig</c> sections
/// (<see cref="JsonReportConfigParser"/>) and the source registry (ADR D42).
/// </summary>
public sealed class PrimitiveObjectConverter : JsonConverter<object?>
{
    /// <inheritdoc />
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => ConvertString(reader.GetString()),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l : reader.GetDouble(),
            _ => JsonDocument.ParseValue(ref reader).RootElement.Clone(),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                break;
        }
    }

    private static object? ConvertString(string? text)
    {
        if (text is null)
            return null;

        // Recover round-tripped ISO-8601 timestamps as DateTime so date parameters bind correctly
        // downstream. RoundtripKind honors any 'Z'/offset and must not be combined with
        // AdjustToUniversal/AssumeUniversal (.NET rejects that pairing).
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dt))
            return dt;

        return text;
    }
}
