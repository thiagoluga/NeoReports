using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

/// <summary>
/// ADR D47: <see cref="ISourceRowCounter"/> against a real SQL Server container — every keyset
/// query here ends in <c>ORDER BY</c>, which SQL Server rejects inside a bare derived table, so a
/// successful count proves the <c>OFFSET 0 ROWS</c> wrap (the same derived-table fix G7 established
/// for the filter translator).
/// </summary>
public class SqlRowCounterTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlRowCounterTests(SqlServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task CountAsync_matches_the_seeded_row_count()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var source = Source.Sql(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 1000);
        var counter = (ISourceRowCounter)source;

        var count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(_fixture.SeededRows);
    }

    /// <summary>
    /// Regression: the dynamic (config-driven, <c>type: "sql"</c>) path shares its
    /// <c>AdoKeysetSource</c> construction with Postgres/MySQL/Oracle via
    /// <c>AdoConfigProperties.CreateAdoConfigSource</c>, which had no way to know it needed SQL
    /// Server's <c>OFFSET 0 ROWS</c> derived-table suffix — every count would have thrown on this
    /// path even though the typed path (above) worked. Proven here, not just by inspection.
    /// </summary>
    [SkippableFact]
    public async Task Config_driven_sql_source_counts_correctly()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var provider = new SqlConfigSourceProvider();
        var config = new SourceConfig("sql", new Dictionary<string, object?>
        {
            ["connectionString"] = _fixture.ConnectionString,
            ["sql"] = Sql,
            ["key"] = "Id",
            ["pageSize"] = 1000,
        });
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer) });

        var source = provider.Create(config, schema, new EmptyServiceProvider());
        var counter = (ISourceRowCounter)source;

        var count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(_fixture.SeededRows);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
