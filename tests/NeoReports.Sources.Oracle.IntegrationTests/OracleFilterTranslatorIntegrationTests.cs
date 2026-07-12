using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

/// <summary>
/// G5 (ADR D45) regression: a filtered preview against a real Oracle database, exercising the exact
/// path <c>ReportPreviewRunner.PreviewFilteredAsync</c> uses end to end — <see cref="AdoFilterTranslator"/>
/// translation, then <see cref="OracleConfigSourceProvider"/> executing the translated SQL — with
/// filter values shaped exactly as they arrive from the preview UI: a plain CLR <c>string</c> (a text
/// input), regardless of the filtered column's real type. Oracle does implicitly convert a text-bound
/// parameter to <c>NUMBER</c> in a comparison, but that conversion follows the session's NLS settings:
/// a value like <c>"2000.00"</c> can fail with <c>ORA-01722</c> against a session whose numeric locale
/// doesn't treat '.' as the decimal separator, so the registration casts numeric filters explicitly
/// (<see cref="AdoFilterTranslator.OracleCast"/>).
/// </summary>
/// <remarks>
/// Filtering the <c>Date</c> column is covered by
/// <see cref="Filtering_the_reserved_word_Date_column_returns_only_matching_rows"/> — it hits
/// ORA-01747 unless the translator quotes it, since "Date" is an Oracle reserved word/datatype
/// name and cannot appear as a bare column reference.
/// </remarks>
[Collection(nameof(OracleCollection))]
public sealed class OracleFilterTranslatorIntegrationTests
{
    private readonly OracleServerFixture _fixture;

    public OracleFilterTranslatorIntegrationTests(OracleServerFixture fixture) => _fixture = fixture;

    // The column is named SaleDate, not Date, in the underlying table (Oracle rejects DATE as a bare
    // column identifier) — aliased back to "Date" here so the filter's Column name matches the schema.
    private const string BaseSql =
        "SELECT Id, Customer, Amount, SaleDate AS \"Date\" FROM Sales " +
        "WHERE (:cursor IS NULL OR Id > :cursor) ORDER BY Id";

    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Customer", ColumnType.String),
        new ReportColumn("Amount", ColumnType.Decimal),
        new ReportColumn("Date", ColumnType.DateTime),
    });

    private static ReportExecutionContext Exec(IReadOnlyDictionary<string, object?> parameters) =>
        new("job", "sales", parameters, NullLogger.Instance, CancellationToken.None);

    private static AdoFilterTranslator OracleTranslator() =>
        new(
            "oracle",
            parameterPrefix: ":",
            castParameter: AdoFilterTranslator.OracleCast,
            quoteIdentifier: AdoFilterTranslator.OracleQuoteIdentifier);

    [SkippableFact]
    public async Task Filtering_a_numeric_column_with_a_decimal_text_bound_value_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        // "2000.00" (a decimal point) is the invariant-culture format every filter value uses —
        // without the NLS-independent cast this fails with ORA-01722 against a session whose
        // numeric locale doesn't treat '.' as the decimal separator.
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "2000.00") };
        OracleTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 2500);

        result.Records.ShouldNotBeEmpty();
        result.Records.ShouldAllBe(r => (decimal)r["Amount"]! > 2000.00m);
    }

    [SkippableFact]
    public async Task Filtering_a_numeric_column_with_a_negative_value_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        // The cast's format model has no explicit sign element — verified empirically against a
        // real Oracle container that a leading '-' still parses correctly with no special handling
        // (adding an explicit S element was tried and instead broke *positive* values, since Oracle
        // then requires an explicit leading '+').
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.LessThan, "-1") };
        OracleTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 10);

        result.Records.ShouldBeEmpty(); // no negative amounts seeded — proves the cast didn't throw, not that rows matched
    }

    [SkippableFact]
    public async Task Filtering_the_reserved_word_Date_column_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        // "Date" is an Oracle reserved word/datatype name — a bare t.Date reference in the
        // translated WHERE clause is rejected with ORA-01747 unless the translator quotes it.
        // "01-JAN-26" (not ISO) because DateTime casting is a separate, still-open gap (see
        // AdoFilterTranslator.OracleCast's remarks) — the session's default NLS_DATE_FORMAT is
        // what implicitly parses the bare text token here, and that format is DD-MON-RR.
        var filters = new[] { new PreviewFilter("Date", PreviewFilterOperator.Equals, "01-JAN-26") };
        OracleTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 2500);

        result.Records.ShouldNotBeEmpty();
        result.Records.ShouldAllBe(r => (DateTime)r["Date"]! == new DateTime(2026, 1, 1));
    }

    [SkippableFact]
    public async Task Filtering_a_string_column_with_equals_returns_only_matching_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "C1") };
        OracleTranslator().TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var filterParameters)
            .ShouldBeTrue();

        BatchResult<ReportRecord> result = await ReadFilteredAsync(translatedSql, filterParameters, pageSize: 10);

        result.Records.Count.ShouldBe(1);
        result.Records[0]["Customer"].ShouldBe("C1");
    }

    private async Task<BatchResult<ReportRecord>> ReadFilteredAsync(
        string translatedSql, IReadOnlyDictionary<string, object?> filterParameters, int pageSize)
    {
        var provider = new OracleConfigSourceProvider();
        var config = new SourceConfig("oracle", new Dictionary<string, object?>
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
