using System.Data.Common;
using Npgsql;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Postgres;

/// <summary>
/// Config-driven PostgreSQL source for the dynamic path (<c>type: "postgres"</c>). Reads its
/// settings from the source <c>properties</c> (<c>connectionString</c>, <c>sql</c>, <c>key</c>,
/// optional <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s.
/// </summary>
public sealed class PostgresConfigSourceProvider : IConfigSourceProvider
{
    private const string Label = "PostgreSQL";

    /// <inheritdoc />
    public string Type => "postgres";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        IReadOnlyDictionary<string, object?>? properties = source.Properties;
        string connectionString = AdoConfigProperties.RequireString(properties, "connectionString", Label);
        string sql = AdoConfigProperties.RequireString(properties, "sql", Label);
        string key = AdoConfigProperties.RequireString(properties, "key", Label);
        int pageSize = AdoConfigProperties.OptionalInt(properties, "pageSize", Label) ?? 1000;

        return new AdoKeysetSource<ReportRecord>(
            () => new NpgsqlConnection(connectionString), sql, key, pageSize, schema,
            parameters: null,
            materialize: (reader, ordinals) => Materialize(reader, ordinals, schema));
    }

    private static ReportRecord Materialize(
        DbDataReader reader, IReadOnlyDictionary<string, int> ordinalByName, ReportSchema schema)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            values[i] = ordinalByName.TryGetValue(schema.Columns[i].Name, out int ordinal) && !reader.IsDBNull(ordinal)
                ? reader.GetValue(ordinal)
                : null;
        }

        return new ReportRecord(schema, values);
    }
}
