namespace NeoReports.Core.Pipeline;

/// <summary>
/// A page after reading, filtering, and projection to schema-ordered values, held per output. Each
/// output may have its own filter and columns (a "view"), so its rows are projected independently in
/// the same single pass over the source. <c>T</c> has been erased at the writer edge.
/// </summary>
/// <param name="Outputs">
/// Projected rows per output, aligned to the compiled report's outputs; each element is that output's
/// page of <c>object?[]</c> rows in its own schema column order.
/// </param>
/// <param name="NextCursor">Opaque cursor for the next page, or <c>null</c> when none.</param>
/// <param name="HasMore">Whether more pages are expected after this one.</param>
/// <param name="RawCount">Number of records read before filtering (for statistics).</param>
/// <param name="WrittenCount">Distinct source records written to at least one output (for statistics).</param>
internal sealed record ProjectedBatch(
    IReadOnlyList<IReadOnlyList<object?[]>> Outputs,
    string? NextCursor,
    bool HasMore,
    int RawCount,
    int WrittenCount);

/// <summary>
/// Reads typed pages from a source, applies each output's filters, and projects to <c>object?[]</c>.
/// Created fresh per execution so per-run state (e.g. a streaming enumerator) is isolated. The typed
/// reading happens without boxing; boxing occurs only during projection, at the writer edge.
/// </summary>
internal interface IProjectedBatchReader : IAsyncDisposable
{
    /// <summary>Reads, filters, and projects the next page (per output).</summary>
    /// <param name="pageNumber">One-based page index.</param>
    /// <param name="cursor">Cursor returned by the previous page, or <c>null</c> on the first.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    Task<ProjectedBatch> ReadAsync(int pageNumber, string? cursor, CancellationToken cancellationToken);
}
