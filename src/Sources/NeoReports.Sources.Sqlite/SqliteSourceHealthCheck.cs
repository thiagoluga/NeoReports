using Microsoft.Data.Sqlite;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Sqlite;

/// <summary>
/// On-demand health check for a registered SQLite source (ADR D42, <c>type: "sqlite"</c>): opens a
/// connection and runs <c>SELECT 1</c>, measuring latency. Bounded by a short timeout so the health
/// endpoint can never hang on an unreachable/locked database file.
/// </summary>
public sealed class SqliteSourceHealthCheck : ISourceHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Type => "sqlite";

    /// <inheritdoc />
    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        AdoSourceHealth.CheckConnectionStringAsync(definition, cs => new SqliteConnection(cs), Timeout, cancellationToken: cancellationToken);
}
