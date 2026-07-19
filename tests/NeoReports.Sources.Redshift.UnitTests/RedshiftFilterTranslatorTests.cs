using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Redshift.UnitTests;

/// <summary>
/// ADR D57: <c>AdoFilterTranslator.TryTranslate</c> is pure string logic — no connection is opened —
/// so its output can be verified without a live cluster. Confirms Redshift is wired with the same
/// <c>::type</c> cast Postgres uses (assumed from Redshift's documented Postgres lineage, not
/// empirically re-verified against a live cluster; see D57).
/// </summary>
public class RedshiftFilterTranslatorTests
{
    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Amount", ColumnType.Decimal),
    });

    private const string BaseSql = "SELECT Id, Amount FROM Sales WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    [Fact]
    public void Numeric_filters_get_the_postgres_style_cast()
    {
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "2000.00") };
        var translator = new AdoFilterTranslator("redshift", castParameter: AdoFilterTranslator.PostgresCast);
        var properties = new Dictionary<string, object?> { ["sql"] = BaseSql };

        translator.TryTranslate(properties, filters, Schema, out var propertyOverrides, out var parameters).ShouldBeTrue();

        ((string)propertyOverrides["sql"]!).ShouldContain("t.Amount > @filter0::numeric");
        parameters["filter0"].ShouldBe("2000.00");
    }
}
