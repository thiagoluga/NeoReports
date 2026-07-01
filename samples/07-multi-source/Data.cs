using NeoReports.Abstractions;

namespace NeoReports.Samples.MultiSource;

/// <summary>A customer (the left/driving side), ordered by <see cref="Id"/>.</summary>
public sealed record Customer(long Id, string Name);

/// <summary>An order (the right side), ordered by <see cref="CustomerId"/>.</summary>
public sealed record Order(long CustomerId, decimal Amount);

/// <summary>The joined result row: a customer with its order roll-up.</summary>
public sealed record CustomerReport(long Id, string Name, int OrderCount, decimal Total);

/// <summary>Trivial in-memory source returning an ordered list as a single page (no database needed).</summary>
internal sealed class InMemorySource<T> : IBatchSource<T>
{
    private readonly IReadOnlyList<T> _rows;

    public InMemorySource(IReadOnlyList<T> rows) => _rows = rows;

    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("k", ColumnType.Integer) });

    public Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
        context.PageNumber == 1
            ? Task.FromResult(new BatchResult<T>(_rows, null, false))
            : Task.FromResult(BatchResult<T>.Empty);
}
