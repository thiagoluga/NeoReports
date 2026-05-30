using NeoReports.Abstractions;

namespace NeoReports.Benchmarks;

/// <summary>Reference row type for the benchmark.</summary>
public sealed record Venda(long Id, string Cliente, decimal Valor, DateTime Data);

/// <summary>
/// In-memory batch source that synthesizes <c>rowCount</c> rows on the fly, one page at a time.
/// It never materializes the full set — each page is generated lazily from the cursor — so the
/// source itself contributes O(pageSize) memory, letting the benchmark isolate the pipeline's
/// own allocation behavior.
/// </summary>
public sealed class SyntheticSource : IBatchSource<Venda>
{
    private readonly long _rowCount;
    private static readonly DateTime BaseDate = new(2026, 1, 1);

    public SyntheticSource(long rowCount, ReportSchema schema)
    {
        _rowCount = rowCount;
        Schema = schema;
    }

    public ReportSchema Schema { get; }

    public Task<BatchResult<Venda>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        // Cursor is the last id emitted (opaque string), null on the first page.
        var lastId = context.Cursor is null ? 0L : long.Parse(context.Cursor, System.Globalization.CultureInfo.InvariantCulture);
        var start = lastId + 1;
        if (start > _rowCount)
            return Task.FromResult(BatchResult<Venda>.Empty);

        var end = Math.Min(start + context.PageSize - 1, _rowCount);
        var count = (int)(end - start + 1);
        var rows = new Venda[count];
        for (var i = 0; i < count; i++)
        {
            var id = start + i;
            rows[i] = new Venda(id, "Cliente", id * 1.5m, BaseDate);
        }

        var hasMore = end < _rowCount;
        var nextCursor = hasMore ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new BatchResult<Venda>(rows, nextCursor, hasMore));
    }
}
