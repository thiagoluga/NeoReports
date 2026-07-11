using NeoReports.Abstractions;
using NeoReports.Sources.Common;

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
    private const string Label = "SQL";

    /// <inheritdoc />
    public string Type => "sql";

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

        return new SqlKeysetSource<ReportRecord>(
            connectionString, sql, key, pageSize, schema,
            parameters: null,
            materialize: (reader, ordinals) => AdoConfigProperties.MaterializeReportRecord(reader, ordinals, schema));
    }
}
