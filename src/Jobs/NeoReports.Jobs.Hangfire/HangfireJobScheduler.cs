using System.Collections.Concurrent;
using global::Hangfire;
using NeoReports.Abstractions;
using NeoReports.Jobs;

namespace NeoReports.Jobs.Hangfire;

/// <summary>
/// Single-server scheduler backed by Hangfire. Each enqueue creates a job record in the
/// <see cref="IJobStore"/> (status tracking) and an enqueued Hangfire background job (execution +
/// persistence). The two ids are mapped in-process so <see cref="CancelAsync"/> can abort the
/// running Hangfire job; the mapping is rebuilt per server run, matching the single-server model
/// (cross-restart cancellation is out of scope — a crashed job restarts from zero, D2).
/// </summary>
public sealed class HangfireJobScheduler : IReportJobScheduler
{
    private readonly IBackgroundJobClient _client;
    private readonly IJobStore _store;
    private readonly ConcurrentDictionary<string, string> _hangfireIdByJobId = new(StringComparer.Ordinal);

    /// <summary>Creates the scheduler.</summary>
    /// <param name="client">Hangfire background job client.</param>
    /// <param name="store">Store used to create and track jobs.</param>
    public HangfireJobScheduler(IBackgroundJobClient client, IJobStore store)
    {
        _client = client;
        _store = store;
    }

    /// <inheritdoc />
    public async Task<string> EnqueueAsync(ReportJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _store.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        var parametersJson = JobParameters.Serialize(request.Parameters);

        // CancellationToken.None here is a placeholder; Hangfire substitutes a real token at run time.
        var hangfireId = _client.Enqueue<HangfireReportJobInvoker>(
            invoker => invoker.ExecuteAsync(job.Id, request.ReportName, parametersJson, CancellationToken.None));

        _hangfireIdByJobId[job.Id] = hangfireId;
        return job.Id;
    }

    /// <inheritdoc />
    public Task<ReportJob?> GetAsync(string jobId, CancellationToken cancellationToken) =>
        _store.GetAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_hangfireIdByJobId.TryGetValue(jobId, out var hangfireId))
        {
            // Deleting the job trips the CancellationToken Hangfire injected into the invoker, so
            // the pipeline stops cooperatively and the worker records a Cancelled status.
            var deleted = _client.Delete(hangfireId);
            return Task.FromResult(deleted);
        }

        return Task.FromResult(false);
    }
}
