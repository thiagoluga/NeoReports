using System.Collections.Concurrent;
using NeoReports.Abstractions;

namespace NeoReports.Jobs;

/// <summary>
/// In-process scheduler that runs each enqueued job on a background <see cref="Task"/>. Suitable
/// for dev and tests; production uses the Hangfire single-server scheduler for persistence across
/// restarts. Cancellation is cooperative via a per-job <see cref="CancellationTokenSource"/>.
/// </summary>
public sealed class InMemoryJobScheduler : IReportJobScheduler, IAsyncDisposable
{
    private readonly IJobStore _store;
    private readonly ReportJobWorker _worker;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);

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

    /// <summary>Cancels all running jobs and waits for their background tasks to unwind.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _running.Values)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        try
        {
            await Task.WhenAll(_tasks.Values).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort drain on shutdown.
        }
    }
}
