using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.MySql.IntegrationTests;

/// <summary>ADR D47: <see cref="ISourceRowCounter"/> against a real MySQL container.</summary>
[Collection(nameof(MySqlServerCollection))]
public class MySqlRowCounterTests
{
    private readonly MySqlServerFixture _fixture;

    public MySqlRowCounterTests(MySqlServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task CountAsync_matches_the_seeded_row_count()
    {
        Skip.IfNot(_fixture.Available, "Docker/MySQL container not available.");

        var source = Source.MySql(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 1000);
        var counter = (ISourceRowCounter)source;

        var count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(_fixture.SeededRows);
    }
}
