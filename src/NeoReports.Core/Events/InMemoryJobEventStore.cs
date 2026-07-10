using System.Collections.Concurrent;

namespace NeoReports.Core.Events;

/// <summary>
/// In-process <see cref="IJobEventStore"/> for tests and single-process dev hosts. Events are lost
/// on restart — for anything that needs to survive a process restart, use <see cref="FileJobEventStore"/>.
/// </summary>
public sealed class InMemoryJobEventStore : IJobEventStore
{
    private readonly JobEventOptions _options;
    private readonly ConcurrentDictionary<string, List<JobEvent>> _byJob = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouched = new();

    /// <summary>Creates a store with default options.</summary>
    public InMemoryJobEventStore() : this(new JobEventOptions())
    {
    }

    /// <summary>Creates a store with the given options.</summary>
    /// <param name="options">Cap/retention options.</param>
    public InMemoryJobEventStore(JobEventOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public Task AppendAsync(JobEvent jobEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobEvent);
        PruneExpired();

        List<JobEvent> list = _byJob.GetOrAdd(jobEvent.JobId, static _ => new List<JobEvent>());
        lock (list)
        {
            if (list.Count >= _options.MaxEventsPerJob)
            {
                if (list.Count == 0 || list[^1].Type != JobEventTypes.EventsTruncated)
                    list.Add(jobEvent with { Type = JobEventTypes.EventsTruncated, Message = null, Data = null });
            }
            else
            {
                list.Add(jobEvent);
            }
        }

        _lastTouched[jobEvent.JobId] = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobEvent>> ListAsync(string jobId, string? type, int limit, int offset, CancellationToken cancellationToken)
    {
        if (!_byJob.TryGetValue(jobId, out List<JobEvent>? list))
            return Task.FromResult<IReadOnlyList<JobEvent>>(Array.Empty<JobEvent>());

        JobEvent[] snapshot;
        lock (list)
            snapshot = list.ToArray();

        IEnumerable<JobEvent> query = snapshot.OrderBy(e => e.Sequence);
        if (type is not null)
            query = query.Where(e => string.Equals(e.Type, type, StringComparison.Ordinal));

        JobEvent[] page = query.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToArray();
        return Task.FromResult<IReadOnlyList<JobEvent>>(page);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        _byJob.TryRemove(jobId, out _);
        _lastTouched.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    private void PruneExpired()
    {
        if (_options.Retention is not { } retention)
            return;

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - retention;
        foreach (KeyValuePair<string, DateTimeOffset> entry in _lastTouched.ToArray())
        {
            if (entry.Value < cutoff)
            {
                _byJob.TryRemove(entry.Key, out _);
                _lastTouched.TryRemove(entry.Key, out _);
            }
        }
    }
}
