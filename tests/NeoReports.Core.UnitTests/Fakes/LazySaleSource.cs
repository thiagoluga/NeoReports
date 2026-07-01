using NeoReports.Abstractions;

namespace NeoReports.Core.UnitTests.Fakes;

/// <summary>
/// Lazy batch source that synthesizes <c>rowCount</c> <see cref="Sale"/> rows one page at a time from
/// the cursor — it never materializes the full set, so each instance contributes O(pageSize) memory.
/// An optional per-page delay lets tests cancel a run mid-flight. Each instance is independent, so it
/// is safe to run many concurrently.
/// </summary>
public sealed class LazySaleSource : IBatchSource<Sale>
{
    private static readonly DateTime BaseDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly long _rowCount;
    private readonly TimeSpan _perPageDelay;

    public LazySaleSource(long rowCount, TimeSpan perPageDelay = default)
    {
        _rowCount = rowCount;
        _perPageDelay = perPageDelay;
    }

    /// <summary>Total pages the source has produced (for observing incremental, page-by-page reads).</summary>
    public int PagesProduced { get; private set; }

    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

    public async Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        if (_perPageDelay > TimeSpan.Zero)
            await Task.Delay(_perPageDelay, cancellationToken).ConfigureAwait(false);

        var lastId = context.Cursor is null
            ? 0L
            : long.Parse(context.Cursor, System.Globalization.CultureInfo.InvariantCulture);
        var start = lastId + 1;
        if (start > _rowCount)
            return BatchResult<Sale>.Empty;

        var end = Math.Min(start + context.PageSize - 1, _rowCount);
        var count = (int)(end - start + 1);
        var rows = new Sale[count];
        for (var i = 0; i < count; i++)
        {
            var id = start + i;
            rows[i] = new Sale(id, "C", id * 1.5m, BaseDate);
        }

        PagesProduced++;
        var hasMore = end < _rowCount;
        var nextCursor = hasMore ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new BatchResult<Sale>(rows, nextCursor, hasMore);
    }
}
