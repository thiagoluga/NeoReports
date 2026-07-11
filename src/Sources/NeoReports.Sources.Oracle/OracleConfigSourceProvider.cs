using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Oracle.ManagedDataAccess.Client;

namespace NeoReports.Sources.Oracle;

/// <summary>
/// Config-driven Oracle source for the dynamic path (<c>type: "oracle"</c>). Reads its settings
/// from the source <c>properties</c> (<c>connectionString</c>, <c>sql</c>, <c>key</c>, optional
/// <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s.
/// </summary>
public sealed class OracleConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "oracle";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services) =>
        AdoConfigProperties.CreateAdoConfigSource(
            cs => new OracleConnection(cs), source, schema, "Oracle", parameterPrefix: ":", configureCommand: Source.ConfigureCommand);
}
