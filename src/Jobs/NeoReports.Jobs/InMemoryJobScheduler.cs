using System.Collections.Concurrent;
using Cronos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Scheduling;

namespace NeoReports.Jobs;

/// <summary>
/// In-process scheduler that runs each enqueued job on a background <see cref="Task"/>. Suitable
/// for dev and tests; production uses the Hangfire single-server scheduler for persistence across
/// restarts. Cancellation is cooperative via a per-job <see cref="CancellationTokenSource"/>.
/// Also implements <see cref="IRecurringReportScheduler"/> (ADR D41): one loop per registered
/// schedule, computing the next occurrence via Cronos and enqueuing through the same
/// <see cref="EnqueueAsync"/> path as any manually triggered run. Schedules die with the process,
/// like everything else in-memory.
/// </summary>
public sealed class InMemoryJobScheduler : IReportJobScheduler, IRecurringReportScheduler, IAsyncDisposable
{
    private readonly IJobStore _store;
    private readonly ReportJobWorker _worker;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Cron, CancellationTokenSource Cts, Task Loop)> _recurring = new(StringComparer.Ordinal);
    // Registering a schedule is remove-then-add, which a ConcurrentDictionary cannot make atomic:
    // two concurrent registrations for one report both removed, both started a loop, and both
    // assigned — so the loser's entry was overwritten without its CTS ever being cancelled and its
    // loop kept firing, untracked, for the process lifetime. Reachable by racing two schedule
    // updates, or one against startup reconciliation. Both methods are synchronous (they return
    // Task.CompletedTask and never await), so a plain lock is the right tool here.
    private readonly object _recurringGate = new();

    // Injected so tests can drive the recurring loop from a fake clock. TimeProvider is BCL (.NET 8),
    // so this costs the shipped package nothing; only the test project takes a dependency, on
    // Microsoft.Extensions.TimeProvider.Testing. Without it the loop is untestable in practice —
    // Cronos granularity is one minute, so every assertion about firing would burn a wall-clock
    // minute of CI, which is why the catch-all below shipped uncovered and had to be reverted (D76).
    private readonly TimeProvider _timeProvider;

    /// <summary>How long the recurring loop waits after an unexpected error before trying again.</summary>
    private static readonly TimeSpan RecurringErrorBackoff = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;

    /// <summary>Creates the scheduler.</summary>
    /// <param name="store">Store used to create and track jobs.</param>
    /// <param name="worker">Worker that executes each job.</param>
    /// <param name="logger">Optional logger; recurring-loop failures are reported through it.</param>
    /// <param name="timeProvider">Optional clock; defaults to the system clock. Tests substitute a fake.</param>
    public InMemoryJobScheduler(
        IJobStore store,
        ReportJobWorker worker,
        ILogger<InMemoryJobScheduler>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _worker = worker;
        _logger = logger ?? (ILogger)NullLogger<InMemoryJobScheduler>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<string> EnqueueAsync(ReportJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _store.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        _running[job.Id] = cts;

        var task = Task.Run(
            async () =>
            {
                try
                {
                    await _worker.RunAsync(job.Id, request.ReportName, request.Parameters, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The worker already recorded the failure in the store; swallow here so the
                    // background task never crashes the process.
                }
                finally
                {
                    _running.TryRemove(job.Id, out _);
                    _tasks.TryRemove(job.Id, out _);
                    cts.Dispose();
                }
            },
            CancellationToken.None);

        _tasks[job.Id] = task;
        return job.Id;
    }

    /// <inheritdoc />
    public Task<ReportJob?> GetAsync(string jobId, CancellationToken cancellationToken) =>
        _store.GetAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }

        // Not running: either unknown or already finished — nothing to cancel.
        return Task.FromResult(false);
    }

    /// <summary>
    /// Awaits a job's background task (test/shutdown helper). Returns immediately if the job is
    /// unknown or already finished.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    public Task WaitForCompletionAsync(string jobId) =>
        _tasks.TryGetValue(jobId, out var task) ? task : Task.CompletedTask;

    /// <inheritdoc />
    public Task RegisterRecurringAsync(string reportName, string cron, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        CronExpression expression = CronValidation.Validate(cron);

        lock (_recurringGate)
        {
            RemoveRecurringEntry(reportName);

            var cts = new CancellationTokenSource();
            Task loop = RunRecurringLoopAsync(reportName, expression, cts);
            _recurring[reportName] = (cron, cts, loop);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveRecurringAsync(string reportName, CancellationToken cancellationToken)
    {
        lock (_recurringGate)
            RemoveRecurringEntry(reportName);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetNextOccurrenceAsync(string reportName, CancellationToken cancellationToken)
    {
        if (!_recurring.TryGetValue(reportName, out var entry))
            return Task.FromResult<DateTimeOffset?>(null);

        CronExpression expression = CronExpression.Parse(entry.Cron);
        DateTime? next = expression.GetNextOccurrence(DateTime.UtcNow);
        return Task.FromResult(next is { } n ? new DateTimeOffset(n, TimeSpan.Zero) : (DateTimeOffset?)null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListRegisteredNamesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_recurring.Keys.ToArray());

    private void RemoveRecurringEntry(string reportName)
    {
        if (_recurring.TryRemove(reportName, out var entry))
            entry.Cts.Cancel();
    }

    // PeriodicTimer's period must fit in ~24.8 days (Int32.MaxValue ms) or its constructor throws —
    // a far-future cron (e.g. yearly) would otherwise fault the loop immediately. Waits longer than
    // this are chunked and re-polled instead of attempted in one call.
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    // Owns cts for its whole lifetime and disposes it on every exit path (natural end, cancellation,
    // or an unexpected exception) — RegisterRecurringAsync/RemoveRecurringAsync only ever Cancel() it.
    private async Task RunRecurringLoopAsync(string reportName, CronExpression expression, CancellationTokenSource cts)
    {
        using (cts)
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (!await WaitForNextOccurrenceAsync(expression, cts.Token).ConfigureAwait(false))
                        return;

                    // Overlapping firings run concurrently (ADR D41) — there is deliberately no
                    // skip-if-running check, because the engine already isolates concurrent runs.
                    await EnqueueAsync(new ReportJobRequest(reportName), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Removed/disposed — stop the loop.
                    return;
                }
                catch (Exception ex)
                {
                    if (!await HandleFiringFailureAsync(reportName, ex, cts.Token).ConfigureAwait(false))
                        return;
                }
            }
        }
    }

    /// <summary>
    /// Sleeps until the next cron occurrence. Returns <see langword="false"/> when the schedule has no
    /// further occurrence or the loop was cancelled while waiting.
    /// </summary>
    private async Task<bool> WaitForNextOccurrenceAsync(CronExpression expression, CancellationToken token)
    {
        DateTime? next = expression.GetNextOccurrence(_timeProvider.GetUtcNow().UtcDateTime);
        if (next is null)
            return false;

        // Waited in chunks rather than one long sleep so cancellation is observed promptly even for a
        // schedule whose next occurrence is hours away.
        TimeSpan remaining = next.Value - _timeProvider.GetUtcNow().UtcDateTime;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan chunk = remaining < MaxWait ? remaining : MaxWait;
            using var timer = new PeriodicTimer(chunk, _timeProvider);
            if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                return false;
            remaining = next.Value - _timeProvider.GetUtcNow().UtcDateTime;
        }

        return !token.IsCancellationRequested;
    }

    /// <summary>
    /// Handles an unexpected failure of one firing. Returns <see langword="true"/> to keep looping.
    /// <para>
    /// This loop is fire-and-forget: nothing awaits it, so an escaping exception used to fault the
    /// task and stop the schedule permanently — no log line, no change in what the API reports, the
    /// report simply never fired again for the life of the process. One bad firing (the store
    /// rejecting a write, say) must not end the schedule, so the failure is logged and the next
    /// occurrence is computed as usual. The back-off keeps a persistent failure from spinning hot.
    /// </para>
    /// </summary>
    private async Task<bool> HandleFiringFailureAsync(string reportName, Exception ex, CancellationToken token)
    {
        _logger.LogError(
            ex,
            "The recurring schedule for report {Report} failed to fire; retrying in {Backoff}.",
            reportName, RecurringErrorBackoff);

        try
        {
            await Task.Delay(RecurringErrorBackoff, _timeProvider, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Cancels all running jobs and recurring loops, and waits for them to unwind.</summary>
    public async ValueTask DisposeAsync()
    {
        // CancelAsync rather than Cancel: cancellation callbacks otherwise run synchronously on the
        // thread disposing the scheduler, so one slow continuation would stall shutdown for the rest.
        // A source already disposed by its own loop is the normal race here — that loop owns its CTS
        // and disposes it on every exit path — so ObjectDisposedException means "already stopped",
        // which is exactly the state this method is trying to reach.
        foreach (var cts in _running.Values)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* already stopped and disposed itself */ }
        }

        foreach (var entry in _recurring.Values)
        {
            try { await entry.Cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* already stopped and disposed itself */ }
        }

        try
        {
            await Task.WhenAll(_tasks.Values.Concat(_recurring.Values.Select(e => e.Loop))).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort drain on shutdown.
        }
    }
}
