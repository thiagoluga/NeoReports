using NeoReports.Abstractions;

namespace NeoReports.Jobs;

/// <summary>
/// No-op checkpoint store for v1. The contract exists so resume-mid-job is possible later, but a
/// crashed job restarts from zero (D2): saving and loading are deliberate no-ops.
/// </summary>
public sealed class NoOpCheckpointStore : ICheckpointStore
{
    /// <inheritdoc />
    public Task SaveAsync(Checkpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<Checkpoint?> LoadAsync(string jobId, CancellationToken cancellationToken) =>
        Task.FromResult<Checkpoint?>(null);

    /// <inheritdoc />
    public Task DeleteAsync(string jobId, CancellationToken cancellationToken) => Task.CompletedTask;
}
