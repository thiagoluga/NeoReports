using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Sql;

/// <summary>
/// Config-driven SQL source for the dynamic path (<c>type: "sql"</c>). Reads its settings from the
/// source <c>properties</c> (<c>connectionString</c>, <c>sql</c>, <c>key</c>, optional
/// <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s. Each row is materialized by reading the result-set column whose name
/// matches each schema column (case-insensitive), reusing the v1 keyset paging engine.
/// </summary>
public sealed class SqlConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "sql";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        var properties = source.Properties;
        var connectionString = RequireString(properties, "connectionString");
        var sql = RequireString(properties, "sql");
        var key = RequireString(properties, "key");
        var pageSize = OptionalInt(properties, "pageSize") ?? 1000;

        return new SqlKeysetSource<ReportRecord>(
            connectionString, sql, key, pageSize, schema,
            parameters: null,
            materialize: (reader, ordinals) => Materialize(reader, ordinals, schema));
    }

    private static ReportRecord Materialize(
        DbDataReader reader, IReadOnlyDictionary<string, int> ordinalByName, ReportSchema schema)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            values[i] = ordinalByName.TryGetValue(schema.Columns[i].Name, out var ordinal) && !reader.IsDBNull(ordinal)
                ? reader.GetValue(ordinal)
                : null;
        }

        return new ReportRecord(schema, values);
    }

    private static string RequireString(IReadOnlyDictionary<string, object?>? properties, string key)
    {
        if (properties is not null
            && properties.TryGetValue(key, out var value)
            && value is string text
            && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new ConfigurationException($"The SQL source requires a non-empty '{key}' property.");
    }

    private static int? OptionalInt(IReadOnlyDictionary<string, object?>? properties, string key)
    {
        if (properties is null || !properties.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            double d => (int)d,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetInt32(),
            _ => throw new ConfigurationException(
                $"The SQL source property '{key}' must be an integer (was {value.GetType().Name})."),
        };
    }
}
