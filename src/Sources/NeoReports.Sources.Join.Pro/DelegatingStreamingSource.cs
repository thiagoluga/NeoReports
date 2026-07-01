using NeoReports.Abstractions;

namespace NeoReports.Sources.Join.Pro;

/// <summary>
/// A thin <see cref="IStreamingSource{T}"/> that produces its rows from a delegate — used by
/// <see cref="Join.MergeJoin{TLeft, TRight, TKey, TResult}"/> to expose the merge as a stream the
/// pipeline slices into batches.
/// </summary>
/// <typeparam name="TResult">The produced row type.</typeparam>
public sealed class DelegatingStreamingSource<TResult> : IStreamingSource<TResult>
{
    private readonly Func<ReportExecutionContext, CancellationToken, IAsyncEnumerable<TResult>> _produce;

    /// <summary>Creates the source.</summary>
    /// <param name="schema">The declared schema (a placeholder; the pipeline projects via the builder's columns).</param>
    /// <param name="produce">Produces the row stream for one execution.</param>
    public DelegatingStreamingSource(
        ReportSchema schema,
        Func<ReportExecutionContext, CancellationToken, IAsyncEnumerable<TResult>> produce)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _produce = produce ?? throw new ArgumentNullException(nameof(produce));
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public IAsyncEnumerable<TResult> ReadAsync(ReportExecutionContext execution, CancellationToken cancellationToken) =>
        _produce(execution, cancellationToken);
}
