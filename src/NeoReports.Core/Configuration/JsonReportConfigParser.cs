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
        options.Converters.Add(new RawJsonStringConverter());
        return options;
    }

    /// <summary>
    /// Reads <c>string</c> properties, additionally allowing an object/array token to be captured as
    /// its raw JSON text. This lets the <c>filter</c> field be written as a JsonLogic object
    /// (<c>"filter": { "==": [...] }</c>) instead of an escaped string; plain string fields are
    /// unaffected.
    /// </summary>
    private sealed class RawJsonStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Null => null,
                JsonTokenType.String => reader.GetString(),
                _ => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
