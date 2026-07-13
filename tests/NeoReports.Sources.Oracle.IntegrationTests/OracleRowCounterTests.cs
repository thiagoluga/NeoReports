using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

/// <summary>ADR D47: <see cref="ISourceRowCounter"/> against a real Oracle container.</summary>
public class OracleRowCounterTests : IClassFixture<OracleServerFixture>
{
    private readonly OracleServerFixture _fixture;

    public OracleRowCounterTests(OracleServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, SaleDate AS \"Date\" FROM Sales " +
        "WHERE (:cursor IS NULL OR Id > :cursor) ORDER BY Id";

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task CountAsync_matches_the_seeded_row_count()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        var source = Source.Oracle(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 1000);
        var counter = (ISourceRowCounter)source;

        var count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(_fixture.SeededRows);
    }
}
