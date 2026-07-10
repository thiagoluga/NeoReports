using System.Collections.Concurrent;
using Cronos;
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

    /// <summary>Creates the scheduler.</summary>
    /// <param name="store">Store used to create and track jobs.</param>
    /// <param name="worker">Worker that executes each job.</param>
    public InMemoryJobScheduler(IJobStore store, ReportJobWorker worker)
    {
        _store = store;
        _worker = worker;
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

        RemoveRecurringEntry(reportName);

        var cts = new CancellationTokenSource();
        Task loop = RunRecurringLoopAsync(reportName, expression, cts);
        _recurring[reportName] = (cron, cts, loop);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveRecurringAsync(string reportName, CancellationToken cancellationToken)
    {
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
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                DateTime? next = expression.GetNextOccurrence(DateTime.UtcNow);
                if (next is null)
                    return;

                TimeSpan remaining = next.Value - DateTime.UtcNow;
                while (remaining > TimeSpan.Zero)
                {
                    TimeSpan chunk = remaining < MaxWait ? remaining : MaxWait;
                    using var timer = new PeriodicTimer(chunk);
                    if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                        return;
                    remaining = next.Value - DateTime.UtcNow;
                }

                if (cts.Token.IsCancellationRequested)
                    return;

                // Overlapping firings run concurrently (ADR D41) — no skip-if-running check; the
                // engine already isolates concurrent job runs.
                await EnqueueAsync(new ReportJobRequest(reportName), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Removed/disposed — stop the loop.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>Cancels all running jobs and recurring loops, and waits for them to unwind.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _running.Values)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        foreach (var entry in _recurring.Values)
        {
            try { entry.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
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
