using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.MongoDb.IntegrationTests;

public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);

public class MongoDbKeysetSourceTests : IClassFixture<MongoDbServerFixture>
{
    private readonly MongoDbServerFixture _fixture;

    public MongoDbKeysetSourceTests(MongoDbServerFixture fixture) => _fixture = fixture;

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task Reads_all_pages_in_order_without_gaps_or_duplicates()
    {
        Skip.IfNot(_fixture.Available, "Docker/MongoDB container not available.");

        var source = Source.MongoDb(_fixture.ConnectionString, _fixture.Database, _fixture.Collection)
            .Keyset<Sale, long>(v => v.Id, pageSize: 1000);

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

        pages.ShouldBe(3); // 2500 documents / 1000 per page
        all.Count.ShouldBe(_fixture.SeededRows);
        all.Select(v => v.Id).ShouldBeInOrder();
        all.Select(v => v.Id).ShouldBeUnique();
        all.Select(v => v.Id).ShouldBe(Enumerable.Range(1, _fixture.SeededRows).Select(i => (long)i));
    }

    [SkippableFact]
    public async Task Materializes_typed_columns()
    {
        Skip.IfNot(_fixture.Available, "Docker/MongoDB container not available.");

        var source = Source.MongoDb(_fixture.ConnectionString, _fixture.Database, _fixture.Collection)
            .Keyset<Sale, long>(v => v.Id, pageSize: 10);
        var result = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(10);
        var first = result.Records[0];
        first.Id.ShouldBe(1);
        first.Customer.ShouldBe("C1");
        first.Date.ShouldBe(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        first.Amount.ShouldBe(1.5m);
    }
}
