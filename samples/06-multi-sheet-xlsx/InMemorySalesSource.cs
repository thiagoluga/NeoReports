using NeoReports.Abstractions;
using NeoReports.Samples.Shared;

namespace NeoReports.Samples.MultiSheetXlsx;

/// <summary>Tiny in-memory batch source so the sample runs with no database. Alternating amounts
/// give both "approved" (positive) and "rejected" (non-positive) rows.</summary>
internal sealed class InMemorySalesSource : IBatchSource<Sale>
{
    private readonly IReadOnlyList<Sale> _sales;

    public InMemorySalesSource(int count)
    {
        var sales = new List<Sale>(count);
        for (var i = 1; i <= count; i++)
        {
            var amount = i % 2 == 0 ? i * 100.5m : -(i * 10m);
            sales.Add(new Sale(i, $"Customer {i}", amount, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i - 1)));
        }

        _sales = sales;
    }

    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

    public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        // A single page: return everything on page 1, then nothing.
        return context.PageNumber == 1
            ? Task.FromResult(new BatchResult<Sale>(_sales, null, false))
            : Task.FromResult(BatchResult<Sale>.Empty);
    }
}
