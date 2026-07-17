using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Npgsql;

namespace NeoReports.Sources.Redshift;

/// <summary>
/// Config-driven Amazon Redshift source for the dynamic path (<c>type: "redshift"</c>). Reads its
/// settings from the source <c>properties</c> (<c>connectionString</c>, <c>sql</c>, <c>key</c>,
/// optional <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s.
/// </summary>
public sealed class RedshiftConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "redshift";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services) =>
        AdoConfigProperties.CreateAdoConfigSource(cs => new NpgsqlConnection(cs), source, schema, "Redshift");
}
