using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);

public sealed record TemporalSale(long Id, DateTime Ts);

[Collection(nameof(OracleCollection))]
public class OracleKeysetSourceTests
{
    private readonly OracleServerFixture _fixture;

    public OracleKeysetSourceTests(OracleServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, SaleDate AS \"Date\" FROM Sales " +
        "WHERE (:cursor IS NULL OR Id > :cursor) ORDER BY Id";

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task Reads_all_pages_in_order_without_gaps_or_duplicates()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        var source = Source.Oracle(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 1000);

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

    [SkippableFact]
    public async Task Materializes_typed_columns()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        var source = Source.Oracle(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);
        var result = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(10);
        var first = result.Records[0];
        first.Id.ShouldBe(1);
        first.Customer.ShouldBe("C1");
        first.Date.ShouldBe(new DateTime(2026, 1, 1));
        first.Amount.ShouldBe(1.5m);
    }

    // Empirically validates the TO_TIMESTAMP format model the QueryBuilder's Oracle dialect now emits
    // for a temporal keyset key (SqlDialect.OracleCast). A DATE/TIMESTAMP-keyed cursor is the codec's
    // ISO-8601 text; without an explicit cast Oracle would implicit-convert it with the session
    // NLS_DATE_FORMAT and throw ORA-01858 on the second page. Five distinct second-granularity
    // timestamps over a page size of 2 force the cursor to round-trip across three pages.
    private const string TemporalSql =
        "SELECT Id, Ts FROM nr_temporal_keyset " +
        "WHERE (:cursor IS NULL OR Ts > TO_TIMESTAMP(:cursor, 'YYYY-MM-DD\"T\"HH24:MI:SS.FF7')) ORDER BY Ts";

    [SkippableFact]
    public async Task A_timestamp_keyset_cursor_round_trips_across_pages()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");
        await SeedTemporalAsync();

        var source = Source.Oracle(_fixture.ConnectionString, TemporalSql).Keyset<TemporalSale, DateTime>(v => v.Ts, pageSize: 2);

        var all = new List<TemporalSale>();
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            var result = await source.ReadBatchAsync(new BatchContext(Exec(), 2, cursor, pages + 1), CancellationToken.None);
            all.AddRange(result.Records);
            pages++;
            if (!result.HasMore)
                break;
            cursor = result.NextCursor;
            cursor.ShouldNotBeNull();
        }

        pages.ShouldBe(3); // 5 rows / 2 per page
        all.Select(v => v.Id).ShouldBe(new long[] { 1, 2, 3, 4, 5 }); // in Ts order, no gaps or duplicates
        all.Select(v => v.Ts).ShouldBeInOrder();
    }

    // Oracle has no CREATE TABLE IF NOT EXISTS and the container is shared assembly-wide, so the DDL
    // runs in a PL/SQL block swallowing "name already used" (ORA-00955) and inserts swallow the
    // unique-constraint violation (ORA-00001) — making the setup safely re-runnable across the suite.
    private async Task SeedTemporalAsync()
    {
        await using var connection = new OracleConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await ExecuteIgnoring(connection,
            "CREATE TABLE nr_temporal_keyset (Id NUMBER(19) PRIMARY KEY, Ts TIMESTAMP NOT NULL)", ignoreCode: -955);
        for (var id = 1; id <= 5; id++)
        {
            await ExecuteIgnoring(connection,
                $"INSERT INTO nr_temporal_keyset (Id, Ts) VALUES ({id}, TIMESTAMP '2026-01-01 00:00:0{id}')", ignoreCode: -1);
        }
        await ExecuteIgnoring(connection, "COMMIT", ignoreCode: 0);
    }

    private static async Task ExecuteIgnoring(OracleConnection connection, string statement, int ignoreCode)
    {
        string body = ignoreCode == 0
            ? statement
            : $"BEGIN EXECUTE IMMEDIATE '{statement.Replace("'", "''", StringComparison.Ordinal)}'; " +
              $"EXCEPTION WHEN OTHERS THEN IF SQLCODE != {ignoreCode} THEN RAISE; END IF; END;";
        await using var command = connection.CreateCommand();
        command.CommandText = body;
        await command.ExecuteNonQueryAsync();
    }
}
