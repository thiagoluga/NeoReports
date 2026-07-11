using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Npgsql;

namespace NeoReports.Sources.Postgres;

/// <summary>
/// Config-driven PostgreSQL source for the dynamic path (<c>type: "postgres"</c>). Reads its
/// settings from the source <c>properties</c> (<c>connectionString</c>, <c>sql</c>, <c>key</c>,
/// optional <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s.
/// </summary>
public sealed class PostgresConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "postgres";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services) =>
        AdoConfigProperties.CreateAdoConfigSource(cs => new NpgsqlConnection(cs), source, schema, "PostgreSQL");
}
