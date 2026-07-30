using Microsoft.Data.SqlClient;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Sql;

/// <summary>
/// On-demand health check for a registered SQL source (ADR D42, <c>type: "sql"</c>): opens a
/// connection and runs <c>SELECT 1</c>, measuring latency. Bounded by a short timeout so the
/// health endpoint can never hang on an unreachable server.
/// </summary>
public sealed class SqlSourceHealthCheck : ISourceHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Type => "sql";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        AdoSourceHealth.CheckConnectionStringAsync(definition, cs => new SqlConnection(cs), Timeout, cancellationToken: cancellationToken);
}
