using System.Diagnostics;
using Microsoft.Data.SqlClient;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

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
    public async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Properties is not { } properties
            || !properties.TryGetValue("connectionString", out var value)
            || value is not string connectionString
            || string.IsNullOrWhiteSpace(connectionString))
        {
            return new SourceHealthResult(Healthy: false, Error: "Source has no 'connectionString' property.", Latency: TimeSpan.Zero);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(timeoutCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(timeoutCts.Token).ConfigureAwait(false);

            stopwatch.Stop();
            return new SourceHealthResult(Healthy: true, Error: null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new SourceHealthResult(Healthy: false, Error: $"Timed out after {Timeout.TotalSeconds:0}s.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SourceHealthResult(Healthy: false, Error: ex.Message, stopwatch.Elapsed);
        }
    }
}
