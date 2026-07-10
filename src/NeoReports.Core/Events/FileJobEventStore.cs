using System.Collections.Concurrent;
using System.Text.Json;

namespace NeoReports.Core.Events;

/// <summary>
/// File-backed <see cref="IJobEventStore"/>: one append-only JSONL file per job id
/// (<c>{Directory}/{jobId}.jsonl</c>), so events survive a process restart on the single-server v1
/// deployment model (D2). Callers must not append concurrently for the same job id — the runner's
/// own emission is always sequential within one job's execution, so no file locking is needed.
/// </summary>
public sealed class FileJobEventStore : IJobEventStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly JobEventOptions _options;
    private readonly ConcurrentDictionary<string, int> _counts = new();
    private readonly ConcurrentDictionary<string, bool> _truncated = new();

    /// <summary>Creates a store with default options.</summary>
    public FileJobEventStore() : this(new JobEventOptions())
    {
    }

    /// <summary>Creates a store with the given options.</summary>
    /// <param name="options">Directory/cap/retention options.</param>
    public FileJobEventStore(JobEventOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task AppendAsync(JobEvent jobEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobEvent);
        PruneExpired();

        if (_truncated.GetValueOrDefault(jobEvent.JobId))
            return;

        int count = _counts.GetOrAdd(jobEvent.JobId, CountExistingEvents);

        JobEvent toWrite = jobEvent;
        if (count >= _options.MaxEventsPerJob)
        {
            toWrite = jobEvent with { Type = JobEventTypes.EventsTruncated, Message = null, Data = null };
            _truncated[jobEvent.JobId] = true;
        }

        Directory.CreateDirectory(_options.Directory);
        string line = JsonSerializer.Serialize(toWrite, Json);
        await File.AppendAllTextAsync(PathFor(jobEvent.JobId), line + Environment.NewLine, cancellationToken).ConfigureAwait(false);

        _counts[jobEvent.JobId] = count + 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobEvent>> ListAsync(string jobId, string? type, int limit, int offset, CancellationToken cancellationToken)
    {
        string path = PathFor(jobId);
        if (!File.Exists(path))
            return Array.Empty<JobEvent>();

        string[] lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        IEnumerable<JobEvent> events = lines
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<JobEvent>(l, Json))
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.Sequence);

        if (type is not null)
            events = events.Where(e => string.Equals(e.Type, type, StringComparison.Ordinal));

        return events.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToArray();
    }

    /// <inheritdoc />
    public Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        _counts.TryRemove(jobId, out _);
        _truncated.TryRemove(jobId, out _);

        try
        {
            string path = PathFor(jobId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort delete: a locked/permission-denied event file must not fail the caller.
        }

        return Task.CompletedTask;
    }

    private int CountExistingEvents(string jobId)
    {
        string path = PathFor(jobId);
        if (!File.Exists(path))
            return 0;

        try
        {
            return File.ReadLines(path).Count(l => l.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void PruneExpired()
    {
        if (_options.Retention is not { } retention || !Directory.Exists(_options.Directory))
            return;

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - retention;
        try
        {
            IEnumerable<string> expired = Directory.EnumerateFiles(_options.Directory, "*.jsonl")
                .Where(file => File.GetLastWriteTimeUtc(file) < cutoff);
            foreach (string file in expired)
            {
                File.Delete(file);
                string jobId = Path.GetFileNameWithoutExtension(file);
                _counts.TryRemove(jobId, out _);
                _truncated.TryRemove(jobId, out _);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort pruning: a locked file must not break appends.
        }
    }

    private string PathFor(string jobId)
    {
        // Job ids are engine-generated GUIDs, but defend against path traversal the same way
        // FileReportConfigStore guards a caller-influenced name.
        string safe = Path.GetFileName(jobId);
        if (string.IsNullOrEmpty(safe) || safe != jobId)
            throw new ArgumentException("Invalid job id.", nameof(jobId));

        // Path.Join (not Path.Combine) — Combine silently drops earlier segments when a later one
        // looks rooted; safe is already proven equal to jobId with no path separators, but Join
        // is the version that can't be fooled by a rooted-looking segment either way.
        return Path.Join(_options.Directory, safe + ".jsonl");
    }
}
