using MySqlConnector;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.MySql;

/// <summary>
/// On-demand health check for a registered MySQL/MariaDB source (ADR D42, <c>type: "mysql"</c>):
/// opens a connection and runs <c>SELECT 1</c>, measuring latency. Bounded by a short timeout so
/// the health endpoint can never hang on an unreachable server.
/// </summary>
public sealed class MySqlSourceHealthCheck : ISourceHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Type => "mysql";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        AdoSourceHealth.CheckConnectionStringAsync(definition, cs => new MySqlConnection(cs), Timeout, cancellationToken: cancellationToken);
}
