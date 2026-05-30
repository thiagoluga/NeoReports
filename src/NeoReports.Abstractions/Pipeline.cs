using Microsoft.Extensions.Logging;

namespace NeoReports.Abstractions;

/// <summary>Relative weight of a job, used to route it to an appropriate queue/worker.</summary>
public enum JobPriority
{
    /// <summary>Small, fast jobs.</summary>
    Light,

    /// <summary>Default weight.</summary>
    Normal,

    /// <summary>Large, resource-intensive jobs.</summary>
    Heavy
}

/// <summary>
/// Ambient state for a single report execution. Renamed from <c>ExecutionContext</c> to avoid
/// the collision with <see cref="System.Threading.ExecutionContext"/>.
/// </summary>
public sealed class ReportExecutionContext
{
    /// <summary>Creates a context for one report execution.</summary>
    /// <param name="jobId">Identifier of the job this execution belongs to.</param>
    /// <param name="reportName">Name of the registered report being executed.</param>
    /// <param name="parameters">Run-time parameters; <c>null</c> is treated as empty.</param>
    /// <param name="logger">Logger scoped to this execution.</param>
    /// <param name="cancellationToken">Token that cancels the execution.</param>
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

    /// <summary>Identifier of the job this execution belongs to.</summary>
    public string JobId { get; }

    /// <summary>Name of the registered report being executed.</summary>
    public string ReportName { get; }

    /// <summary>UTC timestamp captured when the execution started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Token that signals cancellation of the execution.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Parameters supplied at run time (e.g. date ranges).</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Free-form per-execution scratch space for middleware/plugins.</summary>
    public IDictionary<string, object?> Items { get; }

    /// <summary>Logger scoped to this execution.</summary>
    public ILogger Logger { get; }
}

/// <summary>
/// A page of strongly typed records. The batch is the canonical unit of the pipeline:
/// retry, progress, and checkpointing all operate at batch granularity.
/// </summary>
/// <typeparam name="T">The record (row) type produced by the source.</typeparam>
public sealed class ReportBatch<T>
{
    /// <summary>Creates a batch of records read from a source.</summary>
    /// <param name="records">The records in this page.</param>
    /// <param name="pageNumber">Zero-based or one-based page index, as defined by the source.</param>
    /// <param name="cursor">Opaque cursor pointing at the next page, or <c>null</c> when none.</param>
    /// <param name="hasMore">Whether more pages are expected after this one.</param>
    public ReportBatch(IReadOnlyList<T> records, int pageNumber, string? cursor, bool hasMore)
    {
        Records = records ?? throw new ArgumentNullException(nameof(records));
        PageNumber = pageNumber;
        Cursor = cursor;
        HasMore = hasMore;
        ReadAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The records contained in this page.</summary>
    public IReadOnlyList<T> Records { get; }

    /// <summary>Index of this page within the read sequence.</summary>
    public int PageNumber { get; }

    /// <summary>Opaque, serializable keyset cursor for the next page (see <c>BatchResult{T}</c>).</summary>
    public string? Cursor { get; }

    /// <summary>Whether more pages are expected after this one.</summary>
    public bool HasMore { get; }

    /// <summary>UTC timestamp captured when this page was read.</summary>
    public DateTimeOffset ReadAt { get; }

    /// <summary>Number of records in this page.</summary>
    public int Count => Records.Count;
}
