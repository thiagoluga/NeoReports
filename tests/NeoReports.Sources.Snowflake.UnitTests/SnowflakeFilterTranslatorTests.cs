using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Snowflake.UnitTests;

/// <summary>
/// ADR D57: <c>AdoFilterTranslator.TryTranslate</c> is pure string logic — no connection is opened —
/// so its output can be verified without a live warehouse. Confirms Snowflake is wired with the
/// <c>:</c> bind-variable prefix (verified against the driver's own docs, not <c>@</c>) and no
/// explicit cast (Snowflake's documented implicit VARCHAR→NUMBER conversion — not empirically
/// re-verified against a live warehouse; see D57).
/// </summary>
public class SnowflakeFilterTranslatorTests
{
    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Amount", ColumnType.Decimal),
    });

    private const string BaseSql = "SELECT Id, Amount FROM Sales WHERE (:cursor IS NULL OR Id > :cursor) ORDER BY Id";

    [Fact]
    public void Numeric_filters_use_the_colon_prefix_with_no_cast()
    {
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "2000.00") };
        var translator = new AdoFilterTranslator("snowflake", parameterPrefix: ":");

        translator.TryTranslate(BaseSql, filters, Schema, out var translatedSql, out var parameters).ShouldBeTrue();

        translatedSql.ShouldContain("t.Amount > :filter0");
        translatedSql.ShouldNotContain("::"); // no cast — relies on Snowflake's documented implicit conversion
        parameters["filter0"].ShouldBe("2000.00");
    }
}
