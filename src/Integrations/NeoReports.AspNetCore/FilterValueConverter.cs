using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoReports.AspNetCore;

/// <summary>
/// Decodes <see cref="PreviewFilterRequest"/>'s <c>Value</c> as its literal text form, whatever JSON
/// scalar it arrived as — a plain <c>string</c> passes through verbatim, a number keeps its exact
/// written digits (e.g. <c>2000.00</c>, not reformatted), a boolean becomes <c>"true"</c>/<c>"false"</c>.
/// </summary>
/// <remarks>
/// Deliberately does <b>not</b> reuse <see cref="NeoReports.Core.Configuration.PrimitiveObjectConverter"/>
/// (used for config/parameter property bags), which recovers a round-tripped ISO-8601 string as a
/// CLR <see cref="DateTime"/> — the right behavior for its own callers, but wrong here: a filter
/// value that happens to look like a date (an ordinary decimal such as <c>"12.25"</c> parses as
/// December 25) would get silently reinterpreted before <c>AdoFilterTranslator</c> ever sees it,
/// corrupting both <c>Contains</c>/<c>StartsWith</c> patterns (built by wildcarding the literal text)
/// and typed casts (chosen from the column's declared type, not the value's actual runtime type).
/// Keeping every filter value a plain string end to end — matching exactly what the preview UI's
/// text input always sends — makes that invariant checked by the compiler via
/// <see cref="NeoReports.Core.Preview.PreviewFilter"/>'s <c>string?</c> <c>Value</c>, not just documented.
/// </remarks>
public sealed class FilterValueConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            // Preserves the value's exact written digits (e.g. "2000.00") rather than round-tripping
            // through a parsed double/long and reformatting it. JSON numbers are ASCII, so the raw
            // token bytes are the literal text as written.
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
            _ => throw new JsonException("A preview filter value must be a JSON string, number, boolean, or null."),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
