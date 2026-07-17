using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sqlite.IntegrationTests;

/// <summary>ADR D47: <see cref="ISourceRowCounter"/> against a real SQLite file.</summary>
public class SqliteRowCounterTests : IClassFixture<SqliteFileFixture>
{
    private readonly SqliteFileFixture _fixture;

    public SqliteRowCounterTests(SqliteFileFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task CountAsync_matches_the_seeded_row_count()
    {
        var source = Source.Sqlite(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 1000);
        var counter = (ISourceRowCounter)source;

        var count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(_fixture.SeededRows);
    }
}
