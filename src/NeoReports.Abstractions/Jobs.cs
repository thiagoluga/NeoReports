namespace NeoReports.Abstractions;

/// <summary>Lifecycle status of a report job.</summary>
public enum ReportJobStatus
{
    Queued, Running, Completed, Failed, Cancelled, Paused, Retrying
}

/// <summary>Aggregate counters for a running/finished job.</summary>
public sealed record JobStats(
    long RecordsRead = 0,
    long RecordsWritten = 0,
    long BytesWritten = 0,
    int Retries = 0,
    int BatchesProcessed = 0);

/// <summary>A request to enqueue a job for a registered report.</summary>
public sealed class ReportJobRequest
{
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

    public string ReportName { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }
    public JobPriority Priority { get; }
    public string? IdempotencyKey { get; }
    public string? RequestedBy { get; }
}

/// <summary>Persisted state of a report job.</summary>
public sealed class ReportJob
{
    public required string Id { get; init; }
    public required string ReportName { get; init; }
    public required ReportJobStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }
    public JobStats Stats { get; init; } = new();
    public string? RequestedBy { get; init; }
    public string? WorkerId { get; init; }
}

/// <summary>Query filter for listing jobs.</summary>
public sealed record JobQuery
{
    public ReportJobStatus? Status { get; init; }
    public string? ReportName { get; init; }
    public DateTimeOffset? Since { get; init; }
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

/// <summary>Persistence of job state. v1 ships InMemory and a SQL-backed (Hangfire) store.</summary>
public interface IJobStore
{
    Task<ReportJob> CreateAsync(ReportJobRequest request, CancellationToken cancellationToken);
    Task<ReportJob?> GetAsync(string jobId, CancellationToken cancellationToken);
    Task UpdateStatusAsync(string jobId, ReportJobStatus status, string? error, CancellationToken cancellationToken);
    Task UpdateStatsAsync(string jobId, JobStats stats, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReportJob>> ListAsync(JobQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Resumability checkpoint. The contract exists in v1 but the store is a no-op:
/// a crashed job restarts from zero. Mid-job resume (keyset cursor + S3 multipart) is post-MVP.
/// </summary>
public sealed class Checkpoint
{
    public required string JobId { get; init; }
    public int LastCompletedPage { get; init; }

    /// <summary>Opaque serializable cursor (see BatchResult{T}.NextCursor).</summary>
    public string? LastCursor { get; init; }
    public long RecordsProcessed { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Persistence of checkpoints. No-op implementation in v1.</summary>
public interface ICheckpointStore
{
    Task SaveAsync(Checkpoint checkpoint, CancellationToken cancellationToken);
    Task<Checkpoint?> LoadAsync(string jobId, CancellationToken cancellationToken);
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);
}

/// <summary>Enqueues and tracks report jobs. v1: single vertical worker (Hangfire single-server).</summary>
public interface IReportJobScheduler
{
    Task<string> EnqueueAsync(ReportJobRequest request, CancellationToken cancellationToken);
    Task<ReportJob?> GetAsync(string jobId, CancellationToken cancellationToken);
    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken);
}
