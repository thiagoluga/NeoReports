using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>
/// G5 (ADR D45) regression: a filtered preview against a real PostgreSQL database, exercising the
/// exact path <c>ReportPreviewRunner.PreviewFilteredAsync</c> uses end to end — <see cref="AdoFilterTranslator"/>
/// translation, then <see cref="PostgresConfigSourceProvider"/> executing the translated SQL — with
/// filter values shaped exactly as they arrive from the preview UI: a plain CLR <c>string</c> (a text
/// input), regardless of the filtered column's real type. Before the Postgres-cast fix, filtering a
/// NUMERIC/TIMESTAMP column this way failed with "operator does not exist" — Postgres does not
/// implicitly convert a text-bound parameter to those types in a comparison (the same class of gap
/// D43 hit for keyset cursors, but here there is no report-author-controlled SQL text to hand-write a
/// cast into, so the translator itself must add it).
/// </summary>
public sealed class PostgresFilterTranslatorIntegrationTests : IClassFixture<PostgresServerFixture>
{
    private readonly PostgresServerFixture _fixture;

    public PostgresFilterTranslatorIntegrationTests(PostgresServerFixture fixture) => _fixture = fixture;

    private const string BaseSql =
        "SELECT Id, Customer, Amount, Date FROM Sales WHERE (@cursor IS NULL OR Id > @cursor::bigint) ORDER BY Id";

    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Customer", ColumnType.String),
        new ReportColumn("Amount", ColumnType.Decimal),
        new ReportColumn("Date", ColumnType.DateTime),
    });

    private static ReportExecutionContext Exec(IReadOnlyDictionary<string, object?> parameters) =>
        new("job", "sales", parameters, NullLogger.Instance, CancellationToken.None);

    private static AdoFilterTranslator PostgresTranslator() =>
        new("postgres", castParameter: AdoFilterTranslator.PostgresCast);

    [SkippableFact]
    public async Task Filtering_a_numeric_column_with_a_text_bound_value_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");

        // "2000.00" is a CLR string — the shape a filter value always has coming from the preview
        // UI's plain text input, whatever the target column's real type. Seeded amounts top out
        // around 3750.00 (id * 1.5, id up to 2500), so this leaves a sizable matching subset.
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "2000.00") };
        PostgresTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 2500);

        result.Records.ShouldNotBeEmpty();
        result.Records.ShouldAllBe(r => (decimal)r["Amount"]! > 2000.00m);
    }

    [SkippableFact]
    public async Task Filtering_a_timestamp_column_with_a_text_bound_value_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");

        var filters = new[] { new PreviewFilter("Date", PreviewFilterOperator.Equals, "2026-01-01T00:00:00") };
        PostgresTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 10);

        result.Records.ShouldNotBeEmpty();
        result.Records.ShouldAllBe(r => (DateTime)r["Date"]! == new DateTime(2026, 1, 1));
    }

    [SkippableFact]
    public async Task Filtering_a_string_column_with_equals_still_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");

        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "C1") };
        PostgresTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 10);

        result.Records.Count.ShouldBe(1);
        result.Records[0]["Customer"].ShouldBe("C1");
    }

    private async Task<BatchResult<ReportRecord>> ReadFilteredAsync(
        string translatedSql, IReadOnlyDictionary<string, object?> filterParameters, int pageSize)
    {
        var provider = new PostgresConfigSourceProvider();
        var config = new SourceConfig("postgres", new Dictionary<string, object?>
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
