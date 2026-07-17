using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;
using Snowflake.Data.Client;

namespace NeoReports.Sources.Snowflake;

/// <summary>
/// On-demand health check for a registered Snowflake source (ADR D42, <c>type: "snowflake"</c>):
/// opens a connection and runs <c>SELECT 1</c>, measuring latency. Bounded by a short timeout so the
/// health endpoint can never hang on an unreachable warehouse.
/// </summary>
public sealed class SnowflakeSourceHealthCheck : ISourceHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Type => "snowflake";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        AdoSourceHealth.CheckConnectionStringAsync(definition, cs => new SnowflakeDbConnection(cs), Timeout, cancellationToken);
}
