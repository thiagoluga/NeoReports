using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.MongoDb;

/// <summary>
/// On-demand health check for a registered MongoDB source (ADR D42/D44, <c>type: "mongodb"</c>):
/// runs <c>{ ping: 1 }</c> against the server, measuring latency. Bounded by a short timeout so the
/// health endpoint can never hang on an unreachable server.
/// </summary>
public sealed class MongoDbSourceHealthCheck : ISourceHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Type => "mongodb";

    /// <inheritdoc />
    public async Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Properties is not { } properties
            || !properties.TryGetValue("connectionString", out var value)
            || value is not string connectionString
            || string.IsNullOrWhiteSpace(connectionString))
        {
            return new SourceHealthResult(false, "Source has no 'connectionString' property.", TimeSpan.Zero);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = new MongoClient(connectionString);
            await client.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: timeoutCts.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();
            return new SourceHealthResult(true, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new SourceHealthResult(false, $"Timed out after {Timeout.TotalSeconds:0}s.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SourceHealthResult(false, ex.Message, stopwatch.Elapsed);
        }
    }
}
