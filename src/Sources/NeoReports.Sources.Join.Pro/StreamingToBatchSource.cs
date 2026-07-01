using NeoReports.Abstractions;

namespace NeoReports.Sources.Join.Pro;

/// <summary>
/// Adapts an <see cref="IStreamingSource{T}"/> to the <see cref="IBatchSource{T}"/> contract the
/// config compiler expects, paging the stream on demand (<c>context.PageSize</c> rows per batch) so
/// memory stays bounded. A single async enumerator is held across pages of one run and reset when a
/// fresh run begins (signalled by a <c>null</c> cursor), keeping the source re-runnable.
/// </summary>
internal sealed class StreamingToBatchSource<T> : IBatchSource<T>
{
    // Opaque non-null token that keeps the pipeline asking for the next page; the real position lives
    // in the retained enumerator, not in the cursor.
    private const string MorePages = "+";

    private readonly IStreamingSource<T> _inner;
    private IAsyncEnumerator<T>? _enumerator;
    private bool _exhausted;

    public StreamingToBatchSource(IStreamingSource<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Schema = inner.Schema;
    }

    public ReportSchema Schema { get; }

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
