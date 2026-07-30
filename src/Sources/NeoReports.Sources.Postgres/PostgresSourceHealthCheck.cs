using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;
using Npgsql;

namespace NeoReports.Sources.Postgres;

/// <summary>
/// On-demand health check for a registered PostgreSQL source (ADR D42, <c>type: "postgres"</c>):
/// opens a connection and runs <c>SELECT 1</c>, measuring latency. Bounded by a short timeout so
/// the health endpoint can never hang on an unreachable server.
/// </summary>
public sealed class PostgresSourceHealthCheck : ISourceHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Type => "postgres";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        AdoSourceHealth.CheckConnectionStringAsync(definition, cs => new NpgsqlConnection(cs), Timeout, cancellationToken: cancellationToken);
}
