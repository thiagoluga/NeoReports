using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

public sealed record Venda(long Id, string Cliente, decimal Valor, DateTime Data);

public class SqlKeysetSourceTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlKeysetSourceTests(SqlServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Cliente, Valor, Data FROM Vendas " +
        "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private ReportExecutionContext Exec() =>
        new("job", "vendas", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task Reads_all_pages_in_order_without_gaps_or_duplicates()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var source = Source.Sql(_fixture.ConnectionString, Sql).Keyset<Venda, long>(v => v.Id, pageSize: 1000);

        var all = new List<Venda>();
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

    [SkippableFact]
    public async Task Materializes_typed_columns()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var source = Source.Sql(_fixture.ConnectionString, Sql).Keyset<Venda, long>(v => v.Id, pageSize: 10);
        var result = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(10);
        var first = result.Records[0];
        first.Id.ShouldBe(1);
        first.Cliente.ShouldBe("C1");
        first.Data.ShouldBe(new DateTime(2026, 1, 1));
        first.Valor.ShouldBe(1.5m);
    }
}
