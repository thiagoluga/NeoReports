using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeoReports.Abstractions;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Default <see cref="IReportConfigParser"/> that reads a JSON document into a
/// <see cref="ReportConfig"/>. Property names are matched case-insensitively, enums (e.g.
/// <see cref="ColumnType"/>) are read from strings, and free-form <c>properties</c> values are
/// converted to CLR primitives (string, long, double, bool, ISO-8601 <see cref="DateTime"/>) so
/// downstream providers/writers receive usable values instead of raw <see cref="JsonElement"/>s.
/// </summary>
public sealed class JsonReportConfigParser : IReportConfigParser
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <inheritdoc />
    public ReportConfig Parse(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
            throw new ConfigurationException("Report configuration document is empty.");

        ReportConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ReportConfig>(document, Options);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"Invalid report configuration JSON: {ex.Message}", ex);
        }

        if (config is null)
            throw new ConfigurationException("Report configuration JSON deserialized to null.");

        return config;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new PrimitiveObjectConverter());
        return options;
    }

    /// <summary>
    /// Reads JSON values typed as <c>object?</c> (the property-bag values) into CLR primitives.
    /// Nested objects/arrays are preserved as a cloned <see cref="JsonElement"/>.
    /// </summary>
    private sealed class PrimitiveObjectConverter : JsonConverter<object?>
    {
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

        public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case string s:
                    writer.WriteStringValue(s);
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
                case DateTime dt:
                    writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
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

            // Recover round-tripped ISO-8601 timestamps as DateTime so date parameters bind
            // correctly downstream. RoundtripKind honors any 'Z'/offset and must not be combined
            // with AdjustToUniversal/AssumeUniversal (.NET rejects that pairing).
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return dt;

            return text;
        }
    }
}
