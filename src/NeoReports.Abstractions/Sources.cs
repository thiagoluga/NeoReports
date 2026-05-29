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
    /// <summary>Creates a context for reading one page.</summary>
    /// <param name="execution">The ambient execution context.</param>
    /// <param name="pageSize">Maximum number of records to read.</param>
    /// <param name="cursor">Opaque cursor returned by the previous page, or <c>null</c> on the first page.</param>
    /// <param name="pageNumber">Index of the page being read.</param>
    public BatchContext(ReportExecutionContext execution, int pageSize, string? cursor, int pageNumber)
    {
        Execution = execution;
        PageSize = pageSize;
        Cursor = cursor;
        PageNumber = pageNumber;
    }

    /// <summary>The ambient execution context.</summary>
    public ReportExecutionContext Execution { get; }

    /// <summary>Maximum number of records to read for this page.</summary>
    public int PageSize { get; }

    /// <summary>Opaque cursor returned by the previous page; <c>null</c> on the first page.</summary>
    public string? Cursor { get; }

    /// <summary>Index of the page being read.</summary>
    public int PageNumber { get; }
}

/// <summary>Result of reading one page from an <see cref="IBatchSource{T}"/>.</summary>
/// <typeparam name="T">The record (row) type produced by the source.</typeparam>
public sealed class BatchResult<T>
{
    /// <summary>Creates the result of reading one page.</summary>
    /// <param name="records">The records read in this page.</param>
    /// <param name="nextCursor">Opaque cursor for the next page, or <c>null</c> when none.</param>
    /// <param name="hasMore">Whether more pages are expected after this one.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="records"/> is null.</exception>
    public BatchResult(IReadOnlyList<T> records, string? nextCursor, bool hasMore)
    {
        Records = records ?? throw new ArgumentNullException(nameof(records));
        NextCursor = nextCursor;
        HasMore = hasMore;
    }

    /// <summary>The records read in this page.</summary>
    public IReadOnlyList<T> Records { get; }

    /// <summary>
    /// Opaque, serializable keyset token for the next page. The source owns its encoding
    /// (e.g. base64/JSON of the last key). Never expose a raw <c>object</c> cursor.
    /// </summary>
    public string? NextCursor { get; }

    /// <summary>Whether more pages are expected after this one.</summary>
    public bool HasMore { get; }

    /// <summary>An empty result with no records and no further pages.</summary>
    public static BatchResult<T> Empty { get; } = new(Array.Empty<T>(), null, false);
}

/// <summary>
/// Primary source contract: pull one page at a time. Keyset pagination is expressed by the
/// opaque <see cref="BatchResult{T}.NextCursor"/>. This is the canonical model of the pipeline.
/// </summary>
/// <typeparam name="T">The record (row) type produced by the source.</typeparam>
public interface IBatchSource<T> : IReportSource
{
    /// <summary>Reads the next page of records.</summary>
    /// <param name="context">Inputs for this page, including the cursor.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>The page of records plus the cursor for the following page.</returns>
    Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Ergonomic authoring contract for naturally streaming sources. The pipeline adapts the
/// stream into batches internally; it has no execution path of its own.
/// </summary>
/// <typeparam name="T">The record (row) type produced by the source.</typeparam>
public interface IStreamingSource<T> : IReportSource
{
    /// <summary>Streams records; the pipeline slices the stream into batches.</summary>
    /// <param name="execution">The ambient execution context.</param>
    /// <param name="cancellationToken">Token that cancels the stream.</param>
    /// <returns>An asynchronous sequence of records.</returns>
    IAsyncEnumerable<T> ReadAsync(ReportExecutionContext execution, CancellationToken cancellationToken);
}
