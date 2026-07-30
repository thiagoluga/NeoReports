using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;
using Oracle.ManagedDataAccess.Client;

namespace NeoReports.Sources.Oracle;

/// <summary>
/// On-demand health check for a registered Oracle source (ADR D42, <c>type: "oracle"</c>): opens
/// a connection and runs <c>SELECT 1 FROM DUAL</c> (Oracle has no FROM-less SELECT), measuring
/// latency. Bounded by a short timeout so the health endpoint can never hang on an unreachable
/// server.
/// </summary>
public sealed class OracleSourceHealthCheck : ISourceHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Type => "oracle";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        AdoSourceHealth.CheckConnectionStringAsync(definition, cs => new OracleConnection(cs), Timeout, pingSql: "SELECT 1 FROM DUAL", cancellationToken: cancellationToken);
}
