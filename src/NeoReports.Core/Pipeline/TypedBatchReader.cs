using NeoReports.Abstractions;

namespace NeoReports.Core.Pipeline;

/// <summary>One output's projection: the filters to pass and the getters to project with.</summary>
/// <typeparam name="T">The row type produced by the source.</typeparam>
internal sealed record OutputProjection<T>(
    IReadOnlyList<Func<T, bool>> Filters,
    IReadOnlyList<Func<T, object?>> Getters);

/// <summary>
/// Generic reader that bridges a typed source to the non-generic pipeline. Reading and filtering
/// operate on <typeparamref name="T"/> with no boxing; values are boxed into <c>object?[]</c> only
/// during projection, immediately before they are handed to writers. Each output may carry its own
/// filters and columns (a "view"), all projected in one pass over the source.
/// </summary>
/// <typeparam name="T">The row type produced by the source.</typeparam>
internal sealed class TypedBatchReader<T> : IProjectedBatchReader
{
    private readonly IBatchSource<T>? _batchSource;
    private readonly IStreamingSource<T>? _streamingSource;
    private readonly ReportExecutionContext _execution;
    private readonly int _pageSize;
    private readonly IReadOnlyList<OutputProjection<T>> _outputs;

    private IAsyncEnumerator<T>? _enumerator;
    private bool _streamExhausted;

    public TypedBatchReader(
        IBatchSource<T>? batchSource,
        IStreamingSource<T>? streamingSource,
        ReportExecutionContext execution,
        int pageSize,
        IReadOnlyList<OutputProjection<T>> outputs)
    {
        _batchSource = batchSource;
        _streamingSource = streamingSource;
        _execution = execution;
        _pageSize = pageSize;
        _outputs = outputs;
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

        var (outputs, written) = Project(raw);
        return new ProjectedBatch(outputs, nextCursor, hasMore, raw.Count, written);
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

    private (IReadOnlyList<IReadOnlyList<object?[]>> Outputs, int Written) Project(IReadOnlyList<T> raw)
    {
        var buckets = new List<object?[]>[_outputs.Count];
        for (var o = 0; o < buckets.Length; o++)
            buckets[o] = new List<object?[]>(raw.Count);

        var written = 0;
        foreach (var record in raw)
        {
            var includedAnywhere = false;
            for (var o = 0; o < _outputs.Count; o++)
            {
                if (!PassesFilters(record, _outputs[o].Filters))
                    continue;

                var getters = _outputs[o].Getters;
                var values = new object?[getters.Count];
                for (var i = 0; i < getters.Count; i++)
                    values[i] = getters[i](record);

                buckets[o].Add(values);
                includedAnywhere = true;
            }

            if (includedAnywhere)
                written++;
        }

        var outputs = new IReadOnlyList<object?[]>[buckets.Length];
        for (var o = 0; o < buckets.Length; o++)
            outputs[o] = buckets[o];

        return (outputs, written);
    }

    private static bool PassesFilters(T record, IReadOnlyList<Func<T, bool>> filters)
    {
        for (var i = 0; i < filters.Count; i++)
        {
            if (!filters[i](record))
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
