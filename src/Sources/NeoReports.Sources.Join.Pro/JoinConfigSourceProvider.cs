using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Join.Pro;

/// <summary>
/// Config-driven merge-join source for the dynamic path (<c>type: "merge-join"</c>). Composes two
/// nested sources into one by streaming a keyset merge over a shared output column, so a JSON report
/// can join two data sets without any typed code:
/// <code>
/// "source": {
///   "type": "merge-join",
///   "properties": {
///     "key": "customerId",          // the shared column both sides are keyed/ordered by
///     "kind": "leftOuter",          // "inner" (default) or "leftOuter"
///     "left":  { "type": "sql", "properties": { "connectionString": "...", "sql": "...", "key": "customerId" } },
///     "right": { "type": "sql", "properties": { "connectionString": "...", "sql": "...", "key": "customerId" } }
///   }
/// }
/// </code>
/// Both nested sources materialize against the <b>same</b> report schema (each fills the columns its
/// query returns, leaving the rest null); the join then overlays the right side's non-null columns
/// onto the matching left row. Both must be ordered by <c>key</c> — the standard keyset requirement.
/// </summary>
public sealed class JoinConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "merge-join";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(services);

        IReadOnlyDictionary<string, object?> properties =
            source.Properties ?? throw Fail("requires 'left', 'right' and 'key' properties");

        SourceConfig leftConfig = ReadChild(properties, "left");
        SourceConfig rightConfig = ReadChild(properties, "right");
        string keyName = RequireString(properties, "key");
        JoinKind kind = ReadKind(properties);

        int keyIndex = schema.IndexOf(keyName);
        if (keyIndex < 0)
            throw Fail($"key column '{keyName}' is not one of the report columns");

        IBatchSource<ReportRecord> left = BuildChild(leftConfig, schema, services);
        IBatchSource<ReportRecord> right = BuildChild(rightConfig, schema, services);

        object? Key(ReportRecord record) => record[keyIndex];

        IStreamingSource<ReportRecord> joined = Join.MergeJoin(
            left, Key,
            right, Key,
            map: (leftRow, rightRows) => MergeRows(leftRow, rightRows, schema),
            kind: kind,
            keyComparer: ReportKeyComparer.Instance);

        return new StreamingToBatchSource<ReportRecord>(joined);
    }

    private static IBatchSource<ReportRecord> BuildChild(SourceConfig config, ReportSchema schema, IServiceProvider services)
    {
        IConfigSourceProvider provider = services.GetServices<IConfigSourceProvider>()
            .FirstOrDefault(p => string.Equals(p.Type, config.Type, StringComparison.OrdinalIgnoreCase))
            ?? throw Fail($"references child source type '{config.Type}', which has no registered provider");
        return provider.Create(config, schema, services);
    }

    // Overlays each matched right row's non-null columns onto a copy of the left row (same schema
    // order). For a to-one join the group holds one row; with several right rows the last non-null
    // per column wins (documented) — config joins are expected to be to-one lookups.
    private static ReportRecord MergeRows(ReportRecord left, IReadOnlyList<ReportRecord> rights, ReportSchema schema)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
            values[i] = left[i];

        foreach (ReportRecord right in rights)
        {
            for (var i = 0; i < schema.Count; i++)
            {
                if (right[i] is not null)
                    values[i] = right[i];
            }
        }

        return new ReportRecord(schema, values);
    }

    private static SourceConfig ReadChild(IReadOnlyDictionary<string, object?> properties, string name)
    {
        if (!properties.TryGetValue(name, out var raw) || raw is not JsonElement element || element.ValueKind != JsonValueKind.Object)
            throw Fail($"the '{name}' property must be a nested source object");

        string type = GetString(element, "type")
            ?? throw Fail($"the '{name}' source has no 'type'");
        IReadOnlyDictionary<string, object?>? childProperties =
            TryGetObject(element, "properties", out JsonElement props) ? ReadProperties(props) : null;
        return new SourceConfig(type, childProperties);
    }

    private static JoinKind ReadKind(IReadOnlyDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue("kind", out var value) || value is null)
            return JoinKind.Inner;

        if (value is not string text)
            throw Fail("the 'kind' property must be a string");

        return text.Trim().ToLowerInvariant() switch
        {
            "inner" => JoinKind.Inner,
            "leftouter" or "left-outer" or "left" => JoinKind.LeftOuter,
            _ => throw Fail($"has an unknown 'kind' value '{text}' (use 'inner' or 'leftOuter')"),
        };
    }

    private static Dictionary<string, object?> ReadProperties(JsonElement obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in obj.EnumerateObject())
            result[property.Name] = ReadValue(property.Value);
        return result;
    }

    private static object? ReadValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out long l) ? l : value.GetDouble(),
        _ => value.Clone(),
    };

    private static string RequireString(IReadOnlyDictionary<string, object?> properties, string name) =>
        properties.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw Fail($"requires a non-empty '{name}' property");

    private static string? GetString(JsonElement obj, string name) =>
        TryGetProperty(obj, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetObject(JsonElement obj, string name, out JsonElement value)
    {
        if (TryGetProperty(obj, name, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (JsonProperty property in obj.EnumerateObject()
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static ConfigurationException Fail(string message) =>
        new($"The merge-join source {message}.");
}
