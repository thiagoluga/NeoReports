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

        var projected = Project(raw);
        return new ProjectedBatch(
            projected.Outputs, projected.Sectioned, nextCursor, hasMore, raw.Count, projected.Written);
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

    private (IReadOnlyList<IReadOnlyList<object?[]>> Outputs, IReadOnlyList<IReadOnlyList<IReadOnlyList<object?[]>>> Sectioned, int Written) Project(
        IReadOnlyList<T> raw)
    {
        var outputBuckets = new List<object?[]>[_outputs.Count];
        for (var o = 0; o < outputBuckets.Length; o++)
            outputBuckets[o] = new List<object?[]>(raw.Count);

        var sectionedBuckets = new List<object?[]>[_sectioned.Count][];
        for (var s = 0; s < _sectioned.Count; s++)
        {
            sectionedBuckets[s] = new List<object?[]>[_sectioned[s].Count];
            for (var sec = 0; sec < _sectioned[s].Count; sec++)
                sectionedBuckets[s][sec] = [];
        }

        var written = 0;
        foreach (var record in raw)
        {
            var includedAnywhere = false;

            for (var o = 0; o < _outputs.Count; o++)
            {
                if (TryProject(record, _outputs[o], out object?[] row))
                {
                    outputBuckets[o].Add(row);
                    includedAnywhere = true;
                }
            }

            for (var s = 0; s < _sectioned.Count; s++)
            {
                var sections = _sectioned[s];
                for (var sec = 0; sec < sections.Count; sec++)
                {
                    if (TryProject(record, sections[sec], out object?[] row))
                    {
                        sectionedBuckets[s][sec].Add(row);
                        includedAnywhere = true;
                    }
                }
            }

            if (includedAnywhere)
                written++;
        }

        var outputs = new IReadOnlyList<object?[]>[outputBuckets.Length];
        for (var o = 0; o < outputBuckets.Length; o++)
            outputs[o] = outputBuckets[o];

        var sectioned = new IReadOnlyList<IReadOnlyList<object?[]>>[sectionedBuckets.Length];
        for (var s = 0; s < sectionedBuckets.Length; s++)
        {
            var sections = new IReadOnlyList<object?[]>[sectionedBuckets[s].Length];
            for (var sec = 0; sec < sectionedBuckets[s].Length; sec++)
                sections[sec] = sectionedBuckets[s][sec];
            sectioned[s] = sections;
        }

        return (outputs, sectioned, written);
    }

    private static bool TryProject(T record, OutputProjection<T> projection, out object?[] row)
    {
        if (!PassesFilters(record, projection.Filters))
        {
            row = Array.Empty<object?>();
            return false;
        }

        var getters = projection.Getters;
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

    public async ValueTask DisposeAsync()
    {
        if (_enumerator is not null)
            await _enumerator.DisposeAsync().ConfigureAwait(false);
    }
}
