using NeoReports.Abstractions;

namespace NeoReports.Core.Sources;

/// <summary>
/// Adapts an <see cref="IBatchSource{TIn}"/> into an <see cref="IBatchSource{TOut}"/> by applying
/// a typed projection to each record. Used to express the builder's mapping step without changing
/// the row type the builder is generic over.
/// </summary>
internal sealed class MappingBatchSource<TIn, TOut> : IBatchSource<TOut>, ISourceRowCounter
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

    /// <summary>
    /// Forwards to the wrapped source (the map is 1:1, so its count is still correct), or
    /// <c>null</c> when the wrapped source doesn't implement <see cref="ISourceRowCounter"/> — not
    /// a failure, so it must not throw (ADR D47: a wrapper that unconditionally declares this
    /// interface has to say "no total" the same quiet way an unwrapped non-counting source does).
    /// </summary>
    public Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken) =>
        _inner is ISourceRowCounter counter
            ? counter.CountAsync(execution, cancellationToken)
            : Task.FromResult((long?)null);
}

/// <summary>
/// Adapts an <see cref="IStreamingSource{TIn}"/> into an <see cref="IStreamingSource{TOut}"/> by
/// applying a typed projection to each record.
/// </summary>
internal sealed class MappingStreamingSource<TIn, TOut> : IStreamingSource<TOut>, ISourceRowCounter
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

    /// <summary>
    /// Forwards to the wrapped source (the map is 1:1, so its count is still correct), or
    /// <c>null</c> when the wrapped source doesn't implement <see cref="ISourceRowCounter"/> — not
    /// a failure, so it must not throw (ADR D47: a wrapper that unconditionally declares this
    /// interface has to say "no total" the same quiet way an unwrapped non-counting source does).
    /// </summary>
    public Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken) =>
        _inner is ISourceRowCounter counter
            ? counter.CountAsync(execution, cancellationToken)
            : Task.FromResult((long?)null);
}
