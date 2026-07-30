using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;
using Npgsql;

namespace NeoReports.Sources.Redshift;

/// <summary>
/// On-demand health check for a registered Amazon Redshift source (ADR D42, <c>type: "redshift"</c>):
/// opens a connection and runs <c>SELECT 1</c>, measuring latency. Bounded by a short timeout so the
/// health endpoint can never hang on an unreachable cluster.
/// </summary>
public sealed class RedshiftSourceHealthCheck : ISourceHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Type => "redshift";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        AdoSourceHealth.CheckConnectionStringAsync(definition, cs => new NpgsqlConnection(cs), Timeout, cancellationToken: cancellationToken);
}
