using MySqlConnector;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.MySql;

/// <summary>
/// Config-driven MySQL/MariaDB source for the dynamic path (<c>type: "mysql"</c>). Reads its
/// settings from the source <c>properties</c> (<c>connectionString</c>, <c>sql</c>, <c>key</c>,
/// optional <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s.
/// </summary>
public sealed class MySqlConfigSourceProvider : IConfigSourceProvider
{
    private const string Label = "MySQL";

    /// <inheritdoc />
    public string Type => "mysql";

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
            () => new MySqlConnection(connectionString), sql, key, pageSize, schema,
            parameters: null,
            materialize: (reader, ordinals) => AdoConfigProperties.MaterializeReportRecord(reader, ordinals, schema));
    }
}
