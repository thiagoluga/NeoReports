using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Snowflake.Data.Client;

namespace NeoReports.Sources.Snowflake;

/// <summary>
/// Config-driven Snowflake source for the dynamic path (<c>type: "snowflake"</c>). Reads its
/// settings from the source <c>properties</c> (<c>connectionString</c>, <c>sql</c>, <c>key</c>,
/// optional <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s.
/// </summary>
public sealed class SnowflakeConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "snowflake";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services) =>
        AdoConfigProperties.CreateAdoConfigSource(
            cs => new SnowflakeDbConnection(cs), source, schema, "Snowflake", parameterPrefix: ":");
}
