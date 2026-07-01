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
/// during projection, immediately before they are handed to writers. Each output (and each section of
/// a sectioned output) may carry its own filters and columns (a "view"), all projected in one pass.
/// </summary>
/// <typeparam name="T">The row type produced by the source.</typeparam>
internal sealed class TypedBatchReader<T> : IProjectedBatchReader
{
    private readonly IBatchSource<T>? _batchSource;
    private readonly IStreamingSource<T>? _streamingSource;
    private readonly ReportExecutionContext _execution;
    private readonly int _pageSize;
    private readonly IReadOnlyList<OutputProjection<T>> _outputs;
    private readonly IReadOnlyList<IReadOnlyList<OutputProjection<T>>> _sectioned;

    private IAsyncEnumerator<T>? _enumerator;
    private bool _streamExhausted;

    public TypedBatchReader(
        IBatchSource<T>? batchSource,
        IStreamingSource<T>? streamingSource,
        ReportExecutionContext execution,
        int pageSize,
        IReadOnlyList<OutputProjection<T>> outputs,
        IReadOnlyList<IReadOnlyList<OutputProjection<T>>> sectioned)
    {
        _batchSource = batchSource;
        _streamingSource = streamingSource;
        _execution = execution;
        _pageSize = pageSize;
        _outputs = outputs;
        _sectioned = sectioned;
    }

    public async Task<ProjectedBatch> ReadAsync(int pageNumber, string? cursor, CancellationToken cancellationToken)
    {
        IReadOnlyList<T> raw;
        string? nextCursor;
        bool hasMore;

        if (_batchSource is not null)
        {
            var context = new BatchContext(_execution, _pageSize, cursor, pageNumber);
            BatchResult<T> result = await _batchSource.ReadBatchAsync(context, cancellationToken).ConfigureAwait(false);
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

        return BuildBatch(raw, nextCursor, hasMore);
    }

    private ProjectedBatch BuildBatch(IReadOnlyList<T> raw, string? nextCursor, bool hasMore)
    {
        List<object?[]>[] outputBuckets = CreateOutputBuckets(raw.Count);
        List<object?[]>[][] sectionedBuckets = CreateSectionedBuckets();

        // Distribute fills the per-output and per-section buckets as a side effect and returns whether
        // the record landed in any of them; Count tallies the distinct written source rows.
        int written = raw.Count(record => Distribute(record, outputBuckets, sectionedBuckets));

        return new ProjectedBatch(
            FreezeOutputs(outputBuckets), FreezeSectioned(sectionedBuckets), nextCursor, hasMore, raw.Count, written);
    }

    private bool Distribute(T record, List<object?[]>[] outputBuckets, List<object?[]>[][] sectionedBuckets)
    {
        var included = false;

        for (var o = 0; o < _outputs.Count; o++)
        {
            if (TryProject(record, _outputs[o], out object?[] row))
            {
                outputBuckets[o].Add(row);
                included = true;
            }
        }

        for (var s = 0; s < _sectioned.Count; s++)
        {
            IReadOnlyList<OutputProjection<T>> sections = _sectioned[s];
            for (var sec = 0; sec < sections.Count; sec++)
            {
                if (TryProject(record, sections[sec], out object?[] row))
                {
                    sectionedBuckets[s][sec].Add(row);
                    included = true;
                }
            }
        }

        return included;
    }

    private List<object?[]>[] CreateOutputBuckets(int capacity)
    {
        var buckets = new List<object?[]>[_outputs.Count];
        for (var o = 0; o < buckets.Length; o++)
            buckets[o] = new List<object?[]>(capacity);
        return buckets;
    }

    private List<object?[]>[][] CreateSectionedBuckets()
    {
        var buckets = new List<object?[]>[_sectioned.Count][];
        for (var s = 0; s < _sectioned.Count; s++)
        {
            buckets[s] = new List<object?[]>[_sectioned[s].Count];
            for (var sec = 0; sec < _sectioned[s].Count; sec++)
                buckets[s][sec] = [];
        }

        return buckets;
    }

    private static IReadOnlyList<object?[]>[] FreezeOutputs(List<object?[]>[] buckets)
    {
        var outputs = new IReadOnlyList<object?[]>[buckets.Length];
        for (var o = 0; o < buckets.Length; o++)
            outputs[o] = buckets[o];
        return outputs;
    }

    private static IReadOnlyList<IReadOnlyList<object?[]>>[] FreezeSectioned(List<object?[]>[][] buckets)
    {
        var sectioned = new IReadOnlyList<IReadOnlyList<object?[]>>[buckets.Length];
        for (var s = 0; s < buckets.Length; s++)
        {
            var sections = new IReadOnlyList<object?[]>[buckets[s].Length];
            for (var sec = 0; sec < buckets[s].Length; sec++)
                sections[sec] = buckets[s][sec];
            sectioned[s] = sections;
        }

        return sectioned;
    }

    private static bool TryProject(T record, OutputProjection<T> projection, out object?[] row)
    {
        if (!PassesFilters(record, projection.Filters))
        {
            row = Array.Empty<object?>();
            return false;
        }

        IReadOnlyList<Func<T, object?>> getters = projection.Getters;
        row = new object?[getters.Count];
        for (var i = 0; i < getters.Count; i++)
            row[i] = getters[i](record);
        return true;
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

    public async ValueTask DisposeAsync()
    {
        if (_enumerator is not null)
            await _enumerator.DisposeAsync().ConfigureAwait(false);
    }
}
