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
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Properties is not { } properties
            || !properties.TryGetValue("connectionString", out var value)
            || value is not string connectionString
            || string.IsNullOrWhiteSpace(connectionString))
        {
            return Task.FromResult(new SourceHealthResult(Healthy: false, Error: "Source has no 'connectionString' property.", Latency: TimeSpan.Zero));
        }

        return AdoSourceHealth.PingAsync(() => new SqlConnection(connectionString), Timeout, cancellationToken);
    }
}
