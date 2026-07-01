using NeoReports.Abstractions;

namespace NeoReports.Sources.Join;

/// <summary>
/// Wraps a primary <see cref="IBatchSource{T}"/> and transforms each page into enriched result rows
/// via a page-level delegate (built by <see cref="Enrichment.Enrich{TPrimary, TKey, TLookup, TResult}"/>,
/// which does the batched key lookup). Keeps only one page in flight (O(pageSize)); cursor/paging come
/// straight from the primary.
/// </summary>
/// <typeparam name="TPrimary">The primary row type.</typeparam>
/// <typeparam name="TResult">The enriched result row type.</typeparam>
public sealed class EnrichingBatchSource<TPrimary, TResult> : IBatchSource<TResult>
{
    private readonly IBatchSource<TPrimary> _primary;
    private readonly Func<IReadOnlyList<TPrimary>, CancellationToken, Task<IReadOnlyList<TResult>>> _enrichPage;

    /// <summary>Creates an enriching source.</summary>
    /// <param name="primary">The primary source, read page by page.</param>
    /// <param name="enrichPage">Transforms a page of primary rows into enriched result rows.</param>
    public EnrichingBatchSource(
        IBatchSource<TPrimary> primary,
        Func<IReadOnlyList<TPrimary>, CancellationToken, Task<IReadOnlyList<TResult>>> enrichPage)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _enrichPage = enrichPage ?? throw new ArgumentNullException(nameof(enrichPage));
    }

    /// <inheritdoc />
    public ReportSchema Schema => _primary.Schema;

    /// <inheritdoc />
    public async Task<BatchResult<TResult>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        BatchResult<TPrimary> page = await _primary.ReadBatchAsync(context, cancellationToken).ConfigureAwait(false);
        if (page.Records.Count == 0)
            return new BatchResult<TResult>(Array.Empty<TResult>(), page.NextCursor, page.HasMore);

        IReadOnlyList<TResult> results = await _enrichPage(page.Records, cancellationToken).ConfigureAwait(false);
        return new BatchResult<TResult>(results, page.NextCursor, page.HasMore);
    }
}
