using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

/// <summary>
/// G5 (ADR D45) regression: a filtered preview against a real SQL Server database, exercising the
/// exact path <c>ReportPreviewRunner.PreviewFilteredAsync</c> uses end to end — <see cref="AdoFilterTranslator"/>
/// translation, then <see cref="SqlConfigSourceProvider"/> executing the translated SQL — with filter
/// values shaped exactly as they arrive from the preview UI: a plain CLR <c>string</c> (a text
/// input), regardless of the filtered column's real type. Unlike Postgres, SQL Server implicitly
/// converts a text-bound parameter against a numeric/date column in a comparison, so the shared
/// registration (<c>new AdoFilterTranslator("sql")</c>, no cast configured) is expected to just
/// work — this test proves that empirically rather than assuming it.
/// </summary>
public sealed class SqlFilterTranslatorIntegrationTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlFilterTranslatorIntegrationTests(SqlServerFixture fixture) => _fixture = fixture;

    private const string BaseSql =
        "SELECT Id, Customer, Amount, Date FROM Sales WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Customer", ColumnType.String),
        new ReportColumn("Amount", ColumnType.Decimal),
        new ReportColumn("Date", ColumnType.DateTime),
    });

    private static ReportExecutionContext Exec(IReadOnlyDictionary<string, object?> parameters) =>
        new("job", "sales", parameters, NullLogger.Instance, CancellationToken.None);

    // A derived table containing a bare ORDER BY (every keyset query already ends with one) is
    // invalid T-SQL unless followed by TOP, OFFSET, or FOR XML — this matches the real registration.
    private static AdoFilterTranslator SqlTranslator() => new("sql", innerQuerySuffix: " OFFSET 0 ROWS");

    [SkippableFact]
    public async Task Filtering_a_numeric_column_with_a_text_bound_value_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "2000.00") };
        SqlTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 2500);

        result.Records.ShouldNotBeEmpty();
        result.Records.ShouldAllBe(r => (decimal)r["Amount"]! > 2000.00m);
    }

    [SkippableFact]
    public async Task Filtering_a_datetime_column_with_a_text_bound_value_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var filters = new[] { new PreviewFilter("Date", PreviewFilterOperator.Equals, "2026-01-01T00:00:00") };
        SqlTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 10);

        result.Records.ShouldNotBeEmpty();
        result.Records.ShouldAllBe(r => (DateTime)r["Date"]! == new DateTime(2026, 1, 1));
    }

    private async Task<BatchResult<ReportRecord>> ReadFilteredAsync(
        string translatedSql, IReadOnlyDictionary<string, object?> filterParameters, int pageSize)
    {
        var provider = new SqlConfigSourceProvider();
        var config = new SourceConfig("sql", new Dictionary<string, object?>
        {
            ["connectionString"] = _fixture.ConnectionString,
            ["sql"] = translatedSql,
            ["key"] = "Id",
            ["pageSize"] = pageSize,
        });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        return await source.ReadBatchAsync(new BatchContext(Exec(filterParameters), pageSize, null, 1), CancellationToken.None);
    }
}
