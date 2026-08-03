using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sqlite.IntegrationTests;

public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);

public class SqliteKeysetSourceTests : IClassFixture<SqliteFileFixture>
{
    private readonly SqliteFileFixture _fixture;

    public SqliteKeysetSourceTests(SqliteFileFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private static ReportExecutionContext Exec(IReadOnlyDictionary<string, object?>? parameters = null) =>
        new("job", "sales", parameters, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task A_run_time_parameter_cannot_take_over_the_engine_cursor()
    {
        // Regression: run-time parameters used to be bound BEFORE the engine's own @cursor, and
        // AddParameter skips an already-bound name — so a caller passing "cursor" pinned it for every
        // page. The keyset query then kept returning the same first page and the run never advanced
        // (an unbounded loop writing to disk, since the page loop only stops on HasMore=false).
        var hijack = new Dictionary<string, object?> { ["cursor"] = null };
        var source = Source.Sqlite(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);

        var first = await source.ReadBatchAsync(new BatchContext(Exec(hijack), 10, null, 1), CancellationToken.None);
        var second = await source.ReadBatchAsync(
            new BatchContext(Exec(hijack), 10, first.NextCursor, 2), CancellationToken.None);

        first.Records.Count.ShouldBe(10);
        second.Records.Count.ShouldBe(10);
        // The second page must move past the first — not repeat it.
        second.Records[0].Id.ShouldBeGreaterThan(first.Records[^1].Id);
    }

    [Fact]
    public async Task Reads_all_pages_in_order_without_gaps_or_duplicates()
    {
        var source = Source.Sqlite(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 1000);

        var all = new List<Sale>();
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            var result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, cursor, pages + 1), CancellationToken.None);
            all.AddRange(result.Records);
            pages++;
            if (!result.HasMore)
                break;
            cursor = result.NextCursor;
            cursor.ShouldNotBeNull();
        }

        pages.ShouldBe(3); // 2500 rows / 1000 per page
        all.Count.ShouldBe(_fixture.SeededRows);
        all.Select(v => v.Id).ShouldBeInOrder();
        all.Select(v => v.Id).ShouldBeUnique();
        all.Select(v => v.Id).ShouldBe(Enumerable.Range(1, _fixture.SeededRows).Select(i => (long)i));
    }

    [Fact]
    public async Task Materializes_typed_columns()
    {
        var source = Source.Sqlite(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);
        var result = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(10);
        var first = result.Records[0];
        first.Id.ShouldBe(1);
        first.Customer.ShouldBe("C1");
        first.Date.ShouldBe(new DateTime(2026, 1, 1));
        first.Amount.ShouldBe(1.5m);
    }
}
