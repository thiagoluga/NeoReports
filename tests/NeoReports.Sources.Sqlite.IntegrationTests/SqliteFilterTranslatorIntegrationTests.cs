using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sqlite.IntegrationTests;

/// <summary>
/// D45 regression: a filtered preview against a real SQLite database, exercising the exact path
/// <c>ReportPreviewRunner.PreviewFilteredAsync</c> uses end to end — <see cref="AdoFilterTranslator"/>
/// translation, then <see cref="SqliteConfigSourceProvider"/> executing the translated SQL — with
/// filter values shaped exactly as they arrive from the preview UI: a plain CLR <c>string</c> (a text
/// input), regardless of the filtered column's real type. SQLite's operand-affinity rule applies
/// NUMERIC affinity to a text-bound value compared against a NUMERIC/INTEGER/REAL-affinity column, so
/// the shared registration (<c>new AdoFilterTranslator("sqlite")</c>, no cast configured) is expected
/// to just work — this test proves that empirically (ADR D56) rather than assuming it.
/// </summary>
public sealed class SqliteFilterTranslatorIntegrationTests : IClassFixture<SqliteFileFixture>
{
    private readonly SqliteFileFixture _fixture;

    public SqliteFilterTranslatorIntegrationTests(SqliteFileFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task Filtering_a_numeric_column_with_a_text_bound_value_returns_only_matching_rows()
    {
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "2000.00") };
        new AdoFilterTranslator("sqlite").TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 2500);

        result.Records.ShouldNotBeEmpty();
        // Unlike MySqlConnector (native DECIMAL), Microsoft.Data.Sqlite has no DECIMAL storage class —
        // a REAL-affinity column always surfaces as a CLR double through the dynamic path's untyped
        // GetValue (AdoConfigProperties.MaterializeReportRecord does no coercion, unlike the typed
        // path's RecordMaterializer<T>).
        result.Records.ShouldAllBe(r => (double)r["Amount"]! > 2000.00d);
    }

    [Fact]
    public async Task Filtering_a_text_column_with_contains_returns_only_matching_rows()
    {
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Contains, "C25") };
        new AdoFilterTranslator("sqlite").TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 2500);

        result.Records.ShouldNotBeEmpty();
        result.Records.ShouldAllBe(r => ((string)r["Customer"]!).Contains("C25", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Filtering_a_date_column_with_a_text_bound_value_returns_only_matching_rows()
    {
        // Date is TEXT-affinity (zero-padded ISO-8601, e.g. "2026-01-15") — unlike the numeric-affinity
        // case above, this comparison is a plain TEXT-vs-TEXT lexicographic one (no affinity coercion
        // involved at all), which only gives the chronologically correct answer because the seeded
        // fixture's dates are zero-padded (SqliteFileFixture); confirms that assumption empirically.
        var filters = new[] { new PreviewFilter("Date", PreviewFilterOperator.GreaterThan, "2026-01-15") };
        new AdoFilterTranslator("sqlite").TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 2500);

        result.Records.ShouldNotBeEmpty();
        result.Records.ShouldAllBe(r => DateTime.Parse((string)r["Date"]!, CultureInfo.InvariantCulture) > new DateTime(2026, 1, 15));
    }

    private async Task<BatchResult<ReportRecord>> ReadFilteredAsync(
        string translatedSql, IReadOnlyDictionary<string, object?> filterParameters, int pageSize)
    {
        var provider = new SqliteConfigSourceProvider();
        var config = new SourceConfig("sqlite", new Dictionary<string, object?>
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
