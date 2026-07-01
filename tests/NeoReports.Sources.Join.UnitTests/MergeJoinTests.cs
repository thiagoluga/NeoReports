using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Sources.Join;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Join.UnitTests;

/// <summary>
/// B2.2: keyset merge-join of two same-key-ordered sources. For each left row it emits the group of
/// right rows sharing its key; inner drops unmatched left rows, left-outer keeps them with an empty
/// group. Sources page in small chunks here to exercise merging across page boundaries.
/// </summary>
public class MergeJoinTests
{
    private static readonly long[] Matched = { 1, 3 };
    private static readonly long[] All = { 1, 2, 3 };

    private sealed record Customer(long Id, string Name);

    private sealed record Order(long CustomerId, string Item);

    private sealed record Row(long CustomerId, int OrderCount, string Items);

    // customer 1 → 2 orders, customer 2 → none, customer 3 → 1 order (all ordered by key).
    private static IBatchSource<Customer> Customers() =>
        new Paged<Customer>(new[] { new Customer(1, "A"), new Customer(2, "B"), new Customer(3, "C") }, pageSize: 2);

    private static IBatchSource<Order> Orders() =>
        new Paged<Order>(new[] { new Order(1, "x"), new Order(1, "y"), new Order(3, "z") }, pageSize: 2);

    private static Row Map(Customer c, IReadOnlyList<Order> orders) =>
        new(c.Id, orders.Count, string.Join(",", orders.Select(o => o.Item)));

    private static async Task<List<Row>> CollectAsync(IStreamingSource<Row> source)
    {
        var rows = new List<Row>();
        var exec = new ReportExecutionContext("job", "r", null, NullLogger.Instance, CancellationToken.None);
        await foreach (Row row in source.ReadAsync(exec, CancellationToken.None))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task Inner_join_emits_only_matched_left_rows_with_their_group()
    {
        IStreamingSource<Row> joined = Join.MergeJoin(Customers(), c => c.Id, Orders(), o => o.CustomerId, Map);

        List<Row> rows = await CollectAsync(joined);

        rows.Select(r => r.CustomerId).ShouldBe(Matched); // 2 has no orders → dropped
        rows[0].OrderCount.ShouldBe(2);
        rows[0].Items.ShouldBe("x,y"); // both right rows for key 1, in order, across the page boundary
        rows[1].OrderCount.ShouldBe(1);
        rows[1].Items.ShouldBe("z");
    }

    [Fact]
    public async Task Left_outer_join_keeps_unmatched_left_rows_with_an_empty_group()
    {
        IStreamingSource<Row> joined =
            Join.MergeJoin(Customers(), c => c.Id, Orders(), o => o.CustomerId, Map, JoinKind.LeftOuter);

        List<Row> rows = await CollectAsync(joined);

        rows.Select(r => r.CustomerId).ShouldBe(All);
        rows.Single(r => r.CustomerId == 2).OrderCount.ShouldBe(0); // unmatched → empty group
    }

    /// <summary>Pages an ordered list in fixed-size chunks (cursor = next index), regardless of the requested page size.</summary>
    private sealed class Paged<T> : IBatchSource<T>
    {
        private readonly IReadOnlyList<T> _rows;
        private readonly int _pageSize;

        public Paged(IReadOnlyList<T> rows, int pageSize)
        {
            _rows = rows;
            _pageSize = pageSize;
        }

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("k", ColumnType.Integer) });

        public Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            var start = context.Cursor is null ? 0 : int.Parse(context.Cursor, CultureInfo.InvariantCulture);
            if (start >= _rows.Count)
                return Task.FromResult(BatchResult<T>.Empty);

            var take = Math.Min(_pageSize, _rows.Count - start);
            var page = _rows.Skip(start).Take(take).ToArray();
            var end = start + take;
            var hasMore = end < _rows.Count;
            var next = hasMore ? end.ToString(CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new BatchResult<T>(page, next, hasMore));
        }
    }
}
