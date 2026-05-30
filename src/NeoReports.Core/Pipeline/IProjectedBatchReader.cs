namespace NeoReports.Core.Pipeline;

/// <summary>
/// A page after reading, filtering, and projection to schema-ordered values. This is the
/// non-generic shape the pipeline works with — <c>T</c> has been erased at the writer edge.
/// </summary>
/// <param name="Rows">Projected rows; each is an <c>object?[]</c> in schema column order.</param>
/// <param name="NextCursor">Opaque cursor for the next page, or <c>null</c> when none.</param>
/// <param name="HasMore">Whether more pages are expected after this one.</param>
/// <param name="RawCount">Number of records read before filtering (for statistics).</param>
internal sealed record ProjectedBatch(
    IReadOnlyList<object?[]> Rows,
    string? NextCursor,
    bool HasMore,
    int RawCount);

/// <summary>
/// Reads typed pages from a source, applies filters, and projects to <c>object?[]</c>. Created
/// fresh per execution so per-run state (e.g. a streaming enumerator) is isolated. The typed
/// reading happens without boxing; boxing occurs only during projection, at the writer edge.
/// </summary>
internal interface IProjectedBatchReader : IAsyncDisposable
{
    /// <summary>Reads, filters, and projects the next page.</summary>
    /// <param name="pageNumber">One-based page index.</param>
    /// <param name="cursor">Cursor returned by the previous page, or <c>null</c> on the first.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    Task<ProjectedBatch> ReadAsync(int pageNumber, string? cursor, CancellationToken cancellationToken);
}
