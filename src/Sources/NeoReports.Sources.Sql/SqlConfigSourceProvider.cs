using Microsoft.Data.SqlClient;
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
    /// <inheritdoc />
    public string Type => "sql";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services) =>
        AdoConfigProperties.CreateAdoConfigSource(
            cs => new SqlConnection(cs), source, schema, "SQL", countInnerSuffix: SqlServerCount.InnerSuffix);
}
