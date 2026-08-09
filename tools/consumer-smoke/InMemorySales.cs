using NeoReports.Abstractions;

namespace NeoReports.ConsumerSmoke;

/// <summary>A row of the smoke report.</summary>
internal sealed record Sale(long Id, string Customer, decimal Amount);

/// <summary>
/// A source built here rather than taken from a source package, on purpose: implementing
/// <see cref="IBatchSource{T}"/> against the published <c>NeoReports.Abstractions</c> is itself part
/// of what this harness verifies. If the ABI shipped in a state a consumer cannot implement against,
/// this file stops compiling — which is the earliest and loudest possible signal.
/// </summary>
/// <remarks>
/// Every other row is given a non-positive amount so the two worksheets the report sections into are
/// both non-empty. A section that silently produced zero rows would still yield a valid workbook, so
/// the check downstream would pass for the wrong reason.
/// </remarks>
internal sealed class InMemorySales(int total) : IBatchSource<Sale>
{
    private const int PageSize = 7; // not a divisor of any sensible total, so the last page is partial

    /// <summary>
    /// The report's own <c>.Column(...)</c> declarations drive the output, so this source declares an
    /// empty schema rather than duplicating them — a source is only obliged to describe what it can.
    /// </summary>
    public ReportSchema Schema { get; } = new(Array.Empty<ReportColumn>());

    public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        int offset = context.Cursor is null ? 0 : int.Parse(context.Cursor, System.Globalization.CultureInfo.InvariantCulture);
        var page = new List<Sale>(PageSize);

        for (int i = offset; i < offset + PageSize && i < total; i++)
        {
            decimal amount = i % 2 == 0 ? (i + 1) * 10.5m : -(i + 1);
            page.Add(new Sale(i + 1, $"Customer {i + 1}", amount));
        }

        int next = offset + page.Count;
        return Task.FromResult(new BatchResult<Sale>(
            page,
            nextCursor: next < total ? next.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            hasMore: next < total));
    }
}
