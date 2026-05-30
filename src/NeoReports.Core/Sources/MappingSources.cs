using NeoReports.Abstractions;

namespace NeoReports.Core.Sources;

/// <summary>
/// Adapts an <see cref="IBatchSource{TIn}"/> into an <see cref="IBatchSource{TOut}"/> by applying
/// a typed projection to each record. Used to express the builder's mapping step without changing
/// the row type the builder is generic over.
/// </summary>
internal sealed class MappingBatchSource<TIn, TOut> : IBatchSource<TOut>
{
    private readonly IBatchSource<TIn> _inner;
    private readonly Func<TIn, TOut> _map;

    public MappingBatchSource(IBatchSource<TIn> inner, Func<TIn, TOut> map)
    {
        _inner = inner;
        _map = map;
    }

    public ReportSchema Schema => _inner.Schema;

    public async Task<BatchResult<TOut>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        var result = await _inner.ReadBatchAsync(context, cancellationToken).ConfigureAwait(false);

        var mapped = new TOut[result.Records.Count];
        for (var i = 0; i < mapped.Length; i++)
            mapped[i] = _map(result.Records[i]);

        return new BatchResult<TOut>(mapped, result.NextCursor, result.HasMore);
    }
}

/// <summary>
/// Adapts an <see cref="IStreamingSource{TIn}"/> into an <see cref="IStreamingSource{TOut}"/> by
/// applying a typed projection to each record.
/// </summary>
internal sealed class MappingStreamingSource<TIn, TOut> : IStreamingSource<TOut>
{
    private readonly IStreamingSource<TIn> _inner;
    private readonly Func<TIn, TOut> _map;

    public MappingStreamingSource(IStreamingSource<TIn> inner, Func<TIn, TOut> map)
    {
        _inner = inner;
        _map = map;
    }

    public ReportSchema Schema => _inner.Schema;

    public async IAsyncEnumerable<TOut> ReadAsync(
        ReportExecutionContext execution,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _inner.ReadAsync(execution, cancellationToken).ConfigureAwait(false))
            yield return _map(item);
    }
}
