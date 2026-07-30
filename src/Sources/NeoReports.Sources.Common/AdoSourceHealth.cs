using System.Data.Common;
using System.Diagnostics;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Common;

/// <summary>
/// Shared on-demand health check body for relational providers (ADR D42/D43): opens a connection
/// and runs a trivial query, measuring latency. Bounded by a timeout so the health endpoint can
/// never hang on an unreachable server.
/// </summary>
public static class AdoSourceHealth
{
    /// <summary>Opens a connection and runs <paramref name="pingSql"/> (default <c>SELECT 1</c>).</summary>
    /// <param name="connectionFactory">Creates a new, unopened connection.</param>
    /// <param name="timeout">Maximum time allowed for the whole check.</param>
    /// <param name="pingSql">The query to run; must not require a result to be meaningful.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<SourceHealthResult> PingAsync(
        Func<DbConnection> connectionFactory, TimeSpan timeout, string pingSql = "SELECT 1", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using DbConnection connection = connectionFactory();
            await connection.OpenAsync(timeoutCts.Token).ConfigureAwait(false);

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = pingSql;
            await command.ExecuteScalarAsync(timeoutCts.Token).ConfigureAwait(false);

            stopwatch.Stop();
            return new SourceHealthResult(Healthy: true, Error: null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new SourceHealthResult(Healthy: false, Error: $"Timed out after {timeout.TotalSeconds:0}s.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SourceHealthResult(Healthy: false, Error: ex.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Reads <c>connectionString</c> from <paramref name="definition"/>'s properties and, if
    /// present, pings it — the full body of a relational provider's <c>ISourceHealthCheck</c>.
    /// </summary>
    /// <param name="definition">The source definition being checked.</param>
    /// <param name="connectionFactory">Given the connection string, creates a new, unopened connection.</param>
    /// <param name="timeout">Maximum time allowed for the whole check.</param>
    /// <param name="pingSql">The query to run; must not require a result to be meaningful.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<SourceHealthResult> CheckConnectionStringAsync(
        SourceDefinition definition, Func<string, DbConnection> connectionFactory, TimeSpan timeout,
        string pingSql = "SELECT 1", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Properties is not { } properties
            || !properties.TryGetValue("connectionString", out var value)
            || value is not string connectionString
            || string.IsNullOrWhiteSpace(connectionString))
        {
            return Task.FromResult(new SourceHealthResult(Healthy: false, Error: "Source has no 'connectionString' property.", Latency: TimeSpan.Zero));
        }

        return PingAsync(() => connectionFactory(connectionString), timeout, pingSql, cancellationToken);
    }
}
