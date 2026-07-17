using Microsoft.Data.Sqlite;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Sqlite;

/// <summary>
/// Config-driven SQLite source for the dynamic path (<c>type: "sqlite"</c>). Reads its settings from
/// the source <c>properties</c> (<c>connectionString</c>, <c>sql</c>, <c>key</c>, optional
/// <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s.
/// </summary>
public sealed class SqliteConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "sqlite";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services) =>
        AdoConfigProperties.CreateAdoConfigSource(cs => new SqliteConnection(cs), source, schema, "SQLite");
}
