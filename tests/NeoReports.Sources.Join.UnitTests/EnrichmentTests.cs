using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Sources.Join.Pro;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Join.UnitTests;

/// <summary>
/// B2.1: enrichment reads the primary page by page and, per page, makes exactly one batched lookup
/// (never one per row), mapping each row with its looked-up value. Missing keys map to the default.
/// </summary>
public class EnrichmentTests
{
    private static readonly int[] Page1Orders = { 10, 20 };
    private static readonly int[] Page2Orders = { 30 };
    private static readonly long[] Page1Keys = { 1, 2 };
    private static readonly long[] Page2Keys = { 3 };

    private sealed record Customer(long Id, string Name);

    private sealed record CustomerSummary(long Id, string Name, int Orders);

    private static BatchContext Ctx(int pageNumber, string? cursor) =>
        new(new ReportExecutionContext("job", "r", null, NullLogger.Instance, CancellationToken.None), 10, cursor, pageNumber);

    [Fact]
    public async Task Enriches_each_page_with_a_single_batched_lookup()
    {
        var primary = new PagedCustomers(
            new[] { new Customer(1, "A"), new Customer(2, "B") },
            new[] { new Customer(3, "C") });

        var lookupCalls = new List<IReadOnlyList<long>>();
        IBatchSource<CustomerSummary> enriched = primary.Enrich(
            key: c => c.Id,
            lookup: (keys, _) =>
            {
                lookupCalls.Add(keys);
                IReadOnlyDictionary<long, int> map = keys.ToDictionary(k => k, k => (int)(k * 10));
                return Task.FromResult(map);
            },
            map: (c, orders) => new CustomerSummary(c.Id, c.Name, orders));

        BatchResult<CustomerSummary> page1 = await enriched.ReadBatchAsync(Ctx(1, null), CancellationToken.None);
        page1.Records.Select(r => r.Orders).ShouldBe(Page1Orders);
        page1.HasMore.ShouldBeTrue();

        BatchResult<CustomerSummary> page2 = await enriched.ReadBatchAsync(Ctx(2, page1.NextCursor), CancellationToken.None);
        page2.Records.Select(r => r.Orders).ShouldBe(Page2Orders);
        page2.HasMore.ShouldBeFalse();

        // One batched call per page (not per row), with that page's distinct keys.
        lookupCalls.Count.ShouldBe(2);
        lookupCalls[0].ShouldBe(Page1Keys);
        lookupCalls[1].ShouldBe(Page2Keys);
    }

    [Fact]
    public async Task Missing_keys_map_to_the_default_lookup_value()
    {
        var primary = new PagedCustomers(new[] { new Customer(1, "A"), new Customer(2, "B") });

        IBatchSource<CustomerSummary> enriched = primary.Enrich<Customer, long, int, CustomerSummary>(
            key: c => c.Id,
            lookup: (_, _) => Task.FromResult((IReadOnlyDictionary<long, int>)new Dictionary<long, int> { [1] = 99 }),
            map: (c, orders) => new CustomerSummary(c.Id, c.Name, orders));

        BatchResult<CustomerSummary> page = await enriched.ReadBatchAsync(Ctx(1, null), CancellationToken.None);

        page.Records[0].Orders.ShouldBe(99); // found
        page.Records[1].Orders.ShouldBe(0);  // missing -> default(int)
    }

    /// <summary>In-memory primary that returns one supplied page per page number.</summary>
    private sealed class PagedCustomers : IBatchSource<Customer>
    {
        private readonly IReadOnlyList<Customer>[] _pages;

        public PagedCustomers(params IReadOnlyList<Customer>[] pages) => _pages = pages;

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

        public Task<BatchResult<Customer>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            var index = context.PageNumber - 1;
            if (index >= _pages.Length)
                return Task.FromResult(BatchResult<Customer>.Empty);

            var hasMore = index + 1 < _pages.Length;
            var next = hasMore ? (context.PageNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new BatchResult<Customer>(_pages[index], next, hasMore));
        }
    }
}
