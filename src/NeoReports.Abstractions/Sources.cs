namespace NeoReports.Abstractions;

/// <summary>Marker for any source. Sources expose the schema they can produce.</summary>
public interface IReportSource
{
    /// <summary>The output schema this source declares.</summary>
    ReportSchema Schema { get; }
}

/// <summary>Inputs given to a source when reading a single page.</summary>
public sealed class BatchContext
{
    public BatchContext(ReportExecutionContext execution, int pageSize, string? cursor, int pageNumber)
    {
        Execution = execution;
        PageSize = pageSize;
        Cursor = cursor;
        PageNumber = pageNumber;
    }

    public ReportExecutionContext Execution { get; }
    public int PageSize { get; }

    /// <summary>Opaque cursor returned by the previous page; <c>null</c> on the first page.</summary>
    public string? Cursor { get; }
    public int PageNumber { get; }
}

/// <summary>Result of reading one page from an <see cref="IBatchSource{T}"/>.</summary>
public sealed class BatchResult<T>
{
    public BatchResult(IReadOnlyList<T> records, string? nextCursor, bool hasMore)
    {
        Records = records ?? throw new ArgumentNullException(nameof(records));
        NextCursor = nextCursor;
        HasMore = hasMore;
    }

    public IReadOnlyList<T> Records { get; }

    /// <summary>
    /// Opaque, serializable keyset token for the next page. The source owns its encoding
    /// (e.g. base64/JSON of the last key). Never expose a raw <c>object</c> cursor.
    /// </summary>
    public string? NextCursor { get; }
    public bool HasMore { get; }

    public static BatchResult<T> Empty { get; } = new(Array.Empty<T>(), null, false);
}

/// <summary>
/// Primary source contract: pull one page at a time. Keyset pagination is expressed by the
/// opaque <see cref="BatchResult{T}.NextCursor"/>. This is the canonical model of the pipeline.
/// </summary>
public interface IBatchSource<T> : IReportSource
{
    Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Ergonomic authoring contract for naturally streaming sources. The pipeline adapts the
/// stream into batches internally; it has no execution path of its own.
/// </summary>
public interface IStreamingSource<T> : IReportSource
{
    IAsyncEnumerable<T> ReadAsync(ReportExecutionContext execution, CancellationToken cancellationToken);
}
