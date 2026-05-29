using Microsoft.Extensions.Logging;

namespace NeoReports.Abstractions;

/// <summary>Relative weight of a job, used to route it to an appropriate queue/worker.</summary>
public enum JobPriority { Light, Normal, Heavy }

/// <summary>
/// Ambient state for a single report execution. Renamed from <c>ExecutionContext</c> to avoid
/// the collision with <see cref="System.Threading.ExecutionContext"/>.
/// </summary>
public sealed class ReportExecutionContext
{
    public ReportExecutionContext(
        string jobId,
        string reportName,
        IReadOnlyDictionary<string, object?>? parameters,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        JobId = jobId;
        ReportName = reportName;
        Parameters = parameters ?? new Dictionary<string, object?>();
        Logger = logger;
        CancellationToken = cancellationToken;
        StartedAt = DateTimeOffset.UtcNow;
        Items = new Dictionary<string, object?>();
    }

    public string JobId { get; }
    public string ReportName { get; }
    public DateTimeOffset StartedAt { get; }
    public CancellationToken CancellationToken { get; }

    /// <summary>Parameters supplied at run time (e.g. date ranges).</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Free-form per-execution scratch space for middleware/plugins.</summary>
    public IDictionary<string, object?> Items { get; }

    public ILogger Logger { get; }
}

/// <summary>
/// A page of strongly typed records. The batch is the canonical unit of the pipeline:
/// retry, progress, and checkpointing all operate at batch granularity.
/// </summary>
public sealed class ReportBatch<T>
{
    public ReportBatch(IReadOnlyList<T> records, int pageNumber, string? cursor, bool hasMore)
    {
        Records = records ?? throw new ArgumentNullException(nameof(records));
        PageNumber = pageNumber;
        Cursor = cursor;
        HasMore = hasMore;
        ReadAt = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<T> Records { get; }
    public int PageNumber { get; }

    /// <summary>Opaque, serializable keyset cursor for the next page (see <c>BatchResult{T}</c>).</summary>
    public string? Cursor { get; }
    public bool HasMore { get; }
    public DateTimeOffset ReadAt { get; }
    public int Count => Records.Count;
}
