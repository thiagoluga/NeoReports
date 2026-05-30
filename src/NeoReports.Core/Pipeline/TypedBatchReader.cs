using NeoReports.Abstractions;

namespace NeoReports.Core.Pipeline;

/// <summary>
/// Generic reader that bridges a typed source to the non-generic pipeline. Reading, filtering,
/// and mapping operate on <typeparamref name="T"/> with no boxing; values are boxed into
/// <c>object?[]</c> only during projection, immediately before they are handed to writers.
/// </summary>
/// <typeparam name="T">The row type produced by the source.</typeparam>
internal sealed class TypedBatchReader<T> : IProjectedBatchReader
{
    private readonly IBatchSource<T>? _batchSource;
    private readonly IStreamingSource<T>? _streamingSource;
    private readonly ReportExecutionContext _execution;
    private readonly int _pageSize;
    private readonly IReadOnlyList<Func<T, bool>> _filters;
    private readonly IReadOnlyList<Func<T, object?>> _getters;

    private IAsyncEnumerator<T>? _enumerator;
    private bool _streamExhausted;

    public TypedBatchReader(
        IBatchSource<T>? batchSource,
        IStreamingSource<T>? streamingSource,
        ReportExecutionContext execution,
        int pageSize,
        IReadOnlyList<Func<T, bool>> filters,
        IReadOnlyList<Func<T, object?>> getters)
    {
        _batchSource = batchSource;
        _streamingSource = streamingSource;
        _execution = execution;
        _pageSize = pageSize;
        _filters = filters;
        _getters = getters;
    }

    public async Task<ProjectedBatch> ReadAsync(int pageNumber, string? cursor, CancellationToken cancellationToken)
    {
        IReadOnlyList<T> raw;
        string? nextCursor;
        bool hasMore;

        if (_batchSource is not null)
        {
            var context = new BatchContext(_execution, _pageSize, cursor, pageNumber);
            var result = await _batchSource.ReadBatchAsync(context, cancellationToken).ConfigureAwait(false);
            raw = result.Records;
            nextCursor = result.NextCursor;
            hasMore = result.HasMore;
        }
        else
        {
            raw = await ReadStreamingPageAsync(cancellationToken).ConfigureAwait(false);
            hasMore = !_streamExhausted;
            nextCursor = hasMore ? pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        var rows = Project(raw);
        return new ProjectedBatch(rows, nextCursor, hasMore, raw.Count);
    }

    private async Task<IReadOnlyList<T>> ReadStreamingPageAsync(CancellationToken cancellationToken)
    {
        _enumerator ??= _streamingSource!.ReadAsync(_execution, cancellationToken).GetAsyncEnumerator(cancellationToken);

        var page = new List<T>(_pageSize);
        while (page.Count < _pageSize)
        {
            if (!await _enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                _streamExhausted = true;
                break;
            }

            page.Add(_enumerator.Current);
        }

        return page;
    }

    private object?[][] Project(IReadOnlyList<T> raw)
    {
        var rows = new List<object?[]>(raw.Count);
        foreach (var record in raw)
        {
            if (!PassesFilters(record))
                continue;

            var values = new object?[_getters.Count];
            for (var i = 0; i < _getters.Count; i++)
                values[i] = _getters[i](record);

            rows.Add(values);
        }

        return rows.ToArray();
    }

    private bool PassesFilters(T record)
    {
        for (var i = 0; i < _filters.Count; i++)
        {
            if (!_filters[i](record))
                return false;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_enumerator is not null)
            await _enumerator.DisposeAsync().ConfigureAwait(false);
    }
}
