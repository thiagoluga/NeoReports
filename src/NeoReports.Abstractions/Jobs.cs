namespace NeoReports.Abstractions;

/// <summary>Lifecycle status of a report job.</summary>
public enum ReportJobStatus
{
    /// <summary>Accepted and waiting for a worker.</summary>
    Queued,

    /// <summary>Currently being processed.</summary>
    Running,

    /// <summary>Finished successfully, with every batch written.</summary>
    Completed,

    /// <summary>Aborted by an unrecoverable error.</summary>
    Failed,

    /// <summary>Stopped on request before completion.</summary>
    Cancelled,

    /// <summary>Temporarily suspended (reserved for post-MVP).</summary>
    Paused,

    /// <summary>Waiting to retry after a transient failure.</summary>
    Retrying,

    /// <summary>
    /// Finished, but one or more batches were skipped by the failure strategy, so the output is
    /// missing rows the source held (ADR D75). Distinct from <see cref="Completed"/> because the
    /// skip count never reaches the job record at all — <c>SkippedBatches</c> lives on the runner's
    /// <c>ReportRunResult</c> and is not among <see cref="JobStats"/>'s counters — so before this
    /// value existed, a partial run and a whole one were indistinguishable to every caller of the
    /// job API. A green status nobody can look past is how partial data gets treated as complete.
    /// <para>
    /// Appended at the end of the enum on purpose: the members carry implicit values, so inserting
    /// this next to <see cref="Completed"/> would have renumbered everything after it and silently
    /// reinterpreted any status already persisted as an integer.
    /// </para>
    /// </summary>
    Partial
}

/// <summary>Aggregate counters for a running/finished job.</summary>
/// <param name="RecordsRead">Total records read from the source.</param>
/// <param name="RecordsWritten">Total records written to outputs.</param>
/// <param name="BytesWritten">Total bytes written across outputs.</param>
/// <param name="Retries">Number of batch retries performed.</param>
/// <param name="BatchesProcessed">Number of batches processed.</param>
/// <param name="TotalRecords">
/// Total rows the source reported before the run (ADR D47), or null when progress tracking was
/// disabled, unsupported by the source, or the count itself failed.
/// </param>
public sealed record JobStats(
    long RecordsRead = 0,
    long RecordsWritten = 0,
    long BytesWritten = 0,
    int Retries = 0,
    int BatchesProcessed = 0,
    long? TotalRecords = null);

/// <summary>A request to enqueue a job for a registered report.</summary>
public sealed class ReportJobRequest
{
    /// <summary>Creates a job request for a registered report.</summary>
    /// <param name="reportName">Name of the registered report to run.</param>
    /// <param name="parameters">Run-time parameters; <c>null</c> is treated as empty.</param>
    /// <param name="priority">Relative weight used to route the job.</param>
    /// <param name="idempotencyKey">Optional key used to de-duplicate enqueue requests.</param>
    /// <param name="requestedBy">Optional identity of the requester.</param>
    public ReportJobRequest(
        string reportName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        JobPriority priority = JobPriority.Normal,
        string? idempotencyKey = null,
        string? requestedBy = null)
    {
        ReportName = reportName;
        Parameters = parameters ?? new Dictionary<string, object?>();
        Priority = priority;
        IdempotencyKey = idempotencyKey;
        RequestedBy = requestedBy;
    }

    /// <summary>Name of the registered report to run.</summary>
    public string ReportName { get; }

    /// <summary>Run-time parameters for the execution.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Relative weight used to route the job.</summary>
    public JobPriority Priority { get; }

    /// <summary>Optional key used to de-duplicate enqueue requests.</summary>
    public string? IdempotencyKey { get; }

    /// <summary>Optional identity of the requester.</summary>
    public string? RequestedBy { get; }
}

/// <summary>Persisted state of a report job.</summary>
public sealed class ReportJob
{
    /// <summary>Unique job identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Name of the report this job runs.</summary>
    public required string ReportName { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required ReportJobStatus Status { get; init; }

    /// <summary>UTC timestamp when the job was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp when processing started, if it has.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>UTC timestamp when the job finished, if it has.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Failure reason when <see cref="Status"/> is <see cref="ReportJobStatus.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Aggregate counters for the job.</summary>
    public JobStats Stats { get; init; } = new();

    /// <summary>Optional identity of the requester.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>Identifier of the worker that processed (or is processing) the job.</summary>
    public string? WorkerId { get; init; }
}

/// <summary>Query filter for listing jobs.</summary>
public sealed record JobQuery
{
    /// <summary>Restrict to a single status, or <c>null</c> for any.</summary>
    public ReportJobStatus? Status { get; init; }

    /// <summary>Restrict to a single report name, or <c>null</c> for any.</summary>
    public string? ReportName { get; init; }

    /// <summary>Only return jobs created at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Maximum number of jobs to return.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Number of jobs to skip (paging offset).</summary>
    public int Offset { get; init; }
}

/// <summary>Persistence of job state. v1 ships InMemory and a SQL-backed (Hangfire) store.</summary>
public interface IJobStore
{
    /// <summary>Creates and persists a new job from a request.</summary>
    Task<ReportJob> CreateAsync(ReportJobRequest request, CancellationToken cancellationToken);

    /// <summary>Loads a job by id, or returns <c>null</c> when absent.</summary>
    Task<ReportJob?> GetAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Updates the status (and optional error) of a job.</summary>
    Task UpdateStatusAsync(string jobId, ReportJobStatus status, string? error, CancellationToken cancellationToken);

    /// <summary>Updates the aggregate counters of a job.</summary>
    Task UpdateStatsAsync(string jobId, JobStats stats, CancellationToken cancellationToken);

    /// <summary>Lists jobs matching a query.</summary>
    Task<IReadOnlyList<ReportJob>> ListAsync(JobQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Resumability checkpoint. The contract exists in v1 but the store is a no-op:
/// a crashed job restarts from zero. Mid-job resume (keyset cursor + S3 multipart) is post-MVP.
/// </summary>
public sealed class Checkpoint
{
    /// <summary>Identifier of the job this checkpoint belongs to.</summary>
    public required string JobId { get; init; }

    /// <summary>Index of the last page that completed successfully.</summary>
    public int LastCompletedPage { get; init; }

    /// <summary>Opaque serializable cursor (see BatchResult{T}.NextCursor).</summary>
    public string? LastCursor { get; init; }

    /// <summary>Number of records processed up to this checkpoint.</summary>
    public long RecordsProcessed { get; init; }

    /// <summary>UTC timestamp when this checkpoint was written.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Persistence of checkpoints. No-op implementation in v1.</summary>
public interface ICheckpointStore
{
    /// <summary>Persists a checkpoint.</summary>
    Task SaveAsync(Checkpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>Loads the latest checkpoint for a job, or <c>null</c> when absent.</summary>
    Task<Checkpoint?> LoadAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Deletes any checkpoint for a job.</summary>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);
}

/// <summary>Enqueues and tracks report jobs. v1: single vertical worker (Hangfire single-server).</summary>
public interface IReportJobScheduler
{
    /// <summary>Enqueues a job and returns its id.</summary>
    Task<string> EnqueueAsync(ReportJobRequest request, CancellationToken cancellationToken);

    /// <summary>Loads a job by id, or returns <c>null</c> when absent.</summary>
    Task<ReportJob?> GetAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Requests cooperative cancellation of a job; returns whether it was accepted.</summary>
    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken);
}
