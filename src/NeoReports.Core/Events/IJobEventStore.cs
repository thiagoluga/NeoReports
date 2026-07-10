namespace NeoReports.Core.Events;

/// <summary>Options for a <see cref="IJobEventStore"/> implementation.</summary>
public sealed class JobEventOptions
{
    /// <summary>Hard cap of stored events per job; at the cap one <see cref="JobEventTypes.EventsTruncated"/>
    /// marker is appended and every further event for that job is dropped. Default 1000.</summary>
    public int MaxEventsPerJob { get; set; } = 1000;

    /// <summary>Age after which a job's events may be pruned; <c>null</c> keeps them until <see cref="IJobEventStore.DeleteAsync"/> is called. Default <c>null</c>.</summary>
    public TimeSpan? Retention { get; set; }

    /// <summary>Directory used by the file-backed store. Default <c>./neoreports-events</c>.</summary>
    public string Directory { get; set; } = "./neoreports-events";
}

/// <summary>
/// Records and serves structured per-job lifecycle events (ADR D38) — the shared foundation for the
/// retry-detail, timeline, and processing-rate UI cards. Optional: when no store is registered, the
/// runner emits nothing and behaves exactly as it did before this feature existed.
/// </summary>
/// <remarks>Lives in Core (not the frozen <c>Abstractions</c>) — an engine/host concern, not part of
/// the public plugin contract, the same precedent as <c>IReportArtifactStore</c> (D20).</remarks>
public interface IJobEventStore
{
    /// <summary>Appends an event. Implementations must be safe to call repeatedly for the same job id
    /// from a single sequential caller (the runner never appends concurrently for one job).</summary>
    /// <param name="jobEvent">The event to append.</param>
    /// <param name="cancellationToken">Token that cancels the append.</param>
    Task AppendAsync(JobEvent jobEvent, CancellationToken cancellationToken);

    /// <summary>Events for a job, ascending by <see cref="JobEvent.Sequence"/>; empty when none.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="type">Optional exact-match filter on <see cref="JobEvent.Type"/>.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="offset">Number of matching events to skip.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    Task<IReadOnlyList<JobEvent>> ListAsync(string jobId, string? type, int limit, int offset, CancellationToken cancellationToken);

    /// <summary>Deletes all events for a job (best-effort; no error if absent).</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Token that cancels the delete.</param>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);
}
