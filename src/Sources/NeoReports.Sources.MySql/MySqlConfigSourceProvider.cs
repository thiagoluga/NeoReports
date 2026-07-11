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
    /// <inheritdoc />
    public string Type => "mysql";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services) =>
        AdoConfigProperties.CreateAdoConfigSource(cs => new MySqlConnection(cs), source, schema, "MySQL");
}
