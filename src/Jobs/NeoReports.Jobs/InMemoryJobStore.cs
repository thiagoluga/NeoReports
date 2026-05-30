using System.Collections.Concurrent;
using NeoReports.Abstractions;

namespace NeoReports.Jobs;

/// <summary>
/// Thread-safe in-process <see cref="IJobStore"/> for dev and tests. Jobs live only for the
/// lifetime of the process; a SQL-backed store (e.g. Hangfire) is used for persistence.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, ReportJob> _jobs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<ReportJob> CreateAsync(ReportJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = new ReportJob
        {
            Id = Guid.NewGuid().ToString("N"),
            ReportName = request.ReportName,
            Status = ReportJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            RequestedBy = request.RequestedBy,
        };

        _jobs[job.Id] = job;
        return Task.FromResult(job);
    }

    /// <inheritdoc />
    public Task<ReportJob?> GetAsync(string jobId, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.TryGetValue(jobId, out var job) ? job : null);

    /// <inheritdoc />
    public Task UpdateStatusAsync(string jobId, ReportJobStatus status, string? error, CancellationToken cancellationToken)
    {
        Mutate(jobId, job => Clone(
            job,
            status: status,
            error: error ?? job.Error,
            startedAt: status == ReportJobStatus.Running ? (job.StartedAt ?? DateTimeOffset.UtcNow) : job.StartedAt,
            completedAt: IsTerminal(status) ? (job.CompletedAt ?? DateTimeOffset.UtcNow) : job.CompletedAt));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateStatsAsync(string jobId, JobStats stats, CancellationToken cancellationToken)
    {
        Mutate(jobId, job => Clone(job, stats: stats));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReportJob>> ListAsync(JobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<ReportJob> result = _jobs.Values;
        if (query.Status is { } status)
            result = result.Where(j => j.Status == status);
        if (!string.IsNullOrEmpty(query.ReportName))
            result = result.Where(j => j.ReportName == query.ReportName);
        if (query.Since is { } since)
            result = result.Where(j => j.CreatedAt >= since);

        var page = result
            .OrderByDescending(j => j.CreatedAt)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ReportJob>>(page);
    }

    private void Mutate(string jobId, Func<ReportJob, ReportJob> update)
    {
        // AddOrUpdate's update factory may run more than once under contention; that's safe here
        // because `update` is a pure transform over the current value.
        _jobs.AddOrUpdate(
            jobId,
            _ => throw new KeyNotFoundException($"No job with id '{jobId}'."),
            (_, current) => update(current));
    }

    private static bool IsTerminal(ReportJobStatus status) =>
        status is ReportJobStatus.Completed or ReportJobStatus.Failed or ReportJobStatus.Cancelled;

    // ReportJob is a class with init-only members (Abstractions is frozen, not a record), so we
    // copy it explicitly to produce an updated snapshot.
    private static ReportJob Clone(
        ReportJob job,
        ReportJobStatus? status = null,
        string? error = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        JobStats? stats = null) =>
        new()
        {
            Id = job.Id,
            ReportName = job.ReportName,
            Status = status ?? job.Status,
            CreatedAt = job.CreatedAt,
            StartedAt = startedAt ?? job.StartedAt,
            CompletedAt = completedAt ?? job.CompletedAt,
            Error = error ?? job.Error,
            Stats = stats ?? job.Stats,
            RequestedBy = job.RequestedBy,
            WorkerId = job.WorkerId,
        };
}
