using NeoReports.Abstractions;

namespace NeoReports.Core.Sources;

/// <summary>
/// Adapts an <see cref="IStreamingSource{T}"/> to the <see cref="IBatchSource{T}"/> contract the
/// dynamic-path config compiler expects (<c>IConfigSourceProvider.Create</c> must return an
/// <see cref="IBatchSource{T}"/> — <c>Abstractions</c> is frozen, rule 7), paging the stream on
/// demand (<c>context.PageSize</c> rows per batch) so memory stays bounded. A single async
/// enumerator is held across pages of one run and reset when a fresh run begins (signalled by a
/// <c>null</c> cursor), keeping the source re-runnable. Naturally-streaming sources (a CSV file, an
/// HTTP response body, …) that only need to author an <see cref="IStreamingSource{T}"/> for their
/// typed path (rule 2) reuse this adapter for their dynamic path rather than re-deriving cursor
/// bookkeeping they don't otherwise need — first introduced for <c>NeoReports.Sources.Csv</c> (ADR
/// D58), promoted here because file connectors are MIT and can't depend on
/// <c>NeoReports.Sources.Join.Pro</c>'s own internal copy of this exact adapter.
/// </summary>
/// <typeparam name="T">The record (row) type produced by the wrapped source.</typeparam>
public sealed class StreamingToBatchSource<T> : IBatchSource<T>
{
    // Opaque non-null token that keeps the pipeline asking for the next page; the real position lives
    // in the retained enumerator, not in the cursor.
    private const string MorePages = "+";

    private readonly IStreamingSource<T> _inner;
    private IAsyncEnumerator<T>? _enumerator;
    private bool _exhausted;

    /// <summary>Creates the adapter.</summary>
    /// <param name="inner">The streaming source to page over.</param>
    public StreamingToBatchSource(IStreamingSource<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Schema = inner.Schema;
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Cursor is null)
        {
            if (_enumerator is not null)
                await _enumerator.DisposeAsync().ConfigureAwait(false);
            _enumerator = _inner.ReadAsync(context.Execution, cancellationToken).GetAsyncEnumerator(cancellationToken);
            _exhausted = false;
        }

        if (_exhausted || _enumerator is null)
            return new BatchResult<T>(Array.Empty<T>(), null, false);

        var page = new List<T>(context.PageSize);
        while (page.Count < context.PageSize && await _enumerator.MoveNextAsync().ConfigureAwait(false))
            page.Add(_enumerator.Current);

        var hasMore = page.Count == context.PageSize;
        if (!hasMore)
        {
            _exhausted = true;
            await _enumerator.DisposeAsync().ConfigureAwait(false);
            _enumerator = null;
        }

        return new BatchResult<T>(page, hasMore ? MorePages : null, hasMore);
    }
}
