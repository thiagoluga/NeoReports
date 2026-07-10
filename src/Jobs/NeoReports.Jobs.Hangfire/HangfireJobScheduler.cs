using System.Collections.Concurrent;
using Cronos;
using global::Hangfire;
using global::Hangfire.Storage;
using NeoReports.Abstractions;
using NeoReports.Core.Scheduling;
using NeoReports.Jobs;

namespace NeoReports.Jobs.Hangfire;

/// <summary>
/// Single-server scheduler backed by Hangfire. Each enqueue creates a job record in the
/// <see cref="IJobStore"/> (status tracking) and an enqueued Hangfire background job (execution +
/// persistence). The two ids are mapped in-process so <see cref="CancelAsync"/> can abort the
/// running Hangfire job; the mapping is rebuilt per server run, matching the single-server model
/// (cross-restart cancellation is out of scope — a crashed job restarts from zero, D2).
/// Also implements <see cref="IRecurringReportScheduler"/> (ADR D41) via
/// <see cref="IRecurringJobManager"/>, with recurring-job id <c>neoreports:{reportName}</c>. Unlike
/// <see cref="CancelAsync"/>'s in-process mapping, recurring registrations persist in Hangfire's own
/// storage across restarts — <see cref="GetNextOccurrenceAsync"/> computes from the cron this class
/// tracked at registration time rather than reading Hangfire storage.
/// </summary>
public sealed class HangfireJobScheduler : IReportJobScheduler, IRecurringReportScheduler
{
    private readonly IBackgroundJobClient _client;
    private readonly IJobStore _store;
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly ConcurrentDictionary<string, string> _hangfireIdByJobId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _cronByReportName = new(StringComparer.Ordinal);

    /// <summary>Creates the scheduler.</summary>
    /// <param name="client">Hangfire background job client.</param>
    /// <param name="store">Store used to create and track jobs.</param>
    /// <param name="recurringJobManager">Hangfire's recurring-job manager, backing the recurring-schedule capability.</param>
    public HangfireJobScheduler(IBackgroundJobClient client, IJobStore store, IRecurringJobManager recurringJobManager)
    {
        _client = client;
        _store = store;
        _recurringJobManager = recurringJobManager;
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

    /// <inheritdoc />
    public Task RegisterRecurringAsync(string reportName, string cron, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        CronValidation.Validate(cron);

        _recurringJobManager.AddOrUpdate<HangfireReportJobInvoker>(
            RecurringJobId(reportName),
            invoker => invoker.ExecuteRecurringAsync(reportName, CancellationToken.None),
            cron);
        _cronByReportName[reportName] = cron;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveRecurringAsync(string reportName, CancellationToken cancellationToken)
    {
        _recurringJobManager.RemoveIfExists(RecurringJobId(reportName));
        _cronByReportName.TryRemove(reportName, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetNextOccurrenceAsync(string reportName, CancellationToken cancellationToken)
    {
        if (!_cronByReportName.TryGetValue(reportName, out var cron))
            return Task.FromResult<DateTimeOffset?>(null);

        CronExpression expression = CronExpression.Parse(cron);
        DateTime? next = expression.GetNextOccurrence(DateTime.UtcNow);
        return Task.FromResult(next is { } n ? new DateTimeOffset(n, TimeSpan.Zero) : (DateTimeOffset?)null);
    }

    /// <summary>
    /// Reads Hangfire's own persisted storage (not the in-process map, which is rebuilt per server
    /// run) so orphan detection at startup reconciliation sees registrations left over from a
    /// *previous* process run — e.g. a report deleted while the host was down.
    /// </summary>
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListRegisteredNamesAsync(CancellationToken cancellationToken)
    {
        using IStorageConnection connection = JobStorage.Current.GetConnection();
        IReadOnlyList<string> names = connection.GetRecurringJobs()
            .Select(j => j.Id)
            .Where(id => id.StartsWith(RecurringJobIdPrefix, StringComparison.Ordinal))
            .Select(id => id[RecurringJobIdPrefix.Length..])
            .ToArray();
        return Task.FromResult(names);
    }

    private const string RecurringJobIdPrefix = "neoreports:";

    private static string RecurringJobId(string reportName) => RecurringJobIdPrefix + reportName;
}
