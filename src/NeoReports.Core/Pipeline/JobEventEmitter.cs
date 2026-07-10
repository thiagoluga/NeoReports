using Microsoft.Extensions.Logging;
using NeoReports.Core.Events;

namespace NeoReports.Core.Pipeline;

/// <summary>
/// Best-effort per-run wrapper around an optional <see cref="IJobEventStore"/> (ADR D38): assigns a
/// per-run monotonic sequence continuing from any events already recorded for the job id (a crash
/// re-execution), and never lets a store failure affect the run — failures are logged at Debug and
/// swallowed.
/// </summary>
internal sealed class JobEventEmitter
{
    private readonly IJobEventStore? _store;
    private readonly string _jobId;
    private readonly ILogger _logger;
    private int _sequence;

    private JobEventEmitter(IJobEventStore? store, string jobId, int startingSequence, ILogger logger)
    {
        _store = store;
        _jobId = jobId;
        _sequence = startingSequence;
        _logger = logger;
    }

    /// <summary>True when this run found events already recorded for the job id (a crash re-execution).</summary>
    public bool IsRestart => _sequence > 0;

    /// <summary>Resolves the starting sequence from <paramref name="store"/> (or a no-op emitter when <c>null</c>).</summary>
    /// <param name="store">The event store, or <c>null</c> when none is registered.</param>
    /// <param name="jobId">The job id events are recorded under.</param>
    /// <param name="logger">Logger used for best-effort failure reporting.</param>
    /// <param name="cancellationToken">Token that cancels the initial read.</param>
    public static async Task<JobEventEmitter> CreateAsync(
        IJobEventStore? store, string jobId, ILogger logger, CancellationToken cancellationToken)
    {
        if (store is null)
            return new JobEventEmitter(null, jobId, 0, logger);

        var existing = 0;
        try
        {
            existing = (await store.ListAsync(jobId, null, int.MaxValue, 0, cancellationToken).ConfigureAwait(false)).Count;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read existing job events for {JobId}; assuming none.", jobId);
        }

        return new JobEventEmitter(store, jobId, existing, logger);
    }

    /// <summary>Appends an event; a no-op when no store is registered. Never throws.</summary>
    /// <param name="type">One of the <see cref="JobEventTypes"/> constants.</param>
    /// <param name="message">Optional free-text detail (truncated and sanitized before storage).</param>
    /// <param name="data">Optional structured fields for <paramref name="type"/>.</param>
    /// <param name="cancellationToken">Token that cancels the append.</param>
    public async Task EmitAsync(string type, string? message, IReadOnlyDictionary<string, string>? data, CancellationToken cancellationToken)
    {
        if (_store is null)
            return;

        try
        {
            var jobEvent = new JobEvent(_jobId, ++_sequence, DateTimeOffset.UtcNow, type, SanitizeMessage(message), data);
            await _store.AppendAsync(jobEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not append job event {Type} for {JobId}.", type, _jobId);
        }
    }

    // Job events are a durable, structured log — an exception message flowing into it must be
    // capped and newline-safe, the same log-forging concern FileStoreRegistryHydrator.Sanitize
    // guards against for report names.
    private static string? SanitizeMessage(string? message)
    {
        if (message is null)
            return null;

        var sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length > 500 ? sanitized[..500] : sanitized;
    }
}
