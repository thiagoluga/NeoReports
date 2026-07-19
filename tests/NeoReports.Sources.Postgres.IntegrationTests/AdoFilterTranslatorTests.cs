using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>
/// G5 (ADR D45): <see cref="AdoFilterTranslator"/> translation — pure string/dictionary logic,
/// shared by every relational provider, so it lives in <c>NeoReports.Sources.Common</c> with no
/// database dependency. Assertions target the built <c>propertyOverrides["sql"]</c> text and
/// parameter dictionary directly (never string-concatenated values). Signature generalized by ADR
/// D62 (<c>IFilterTranslator</c> off <c>"sql"</c>) — <c>AdoFilterTranslator</c> now reads
/// <c>"sql"</c> from the passed-in properties bag itself.
/// </summary>
public class AdoFilterTranslatorTests
{
    private const string Sql = "SELECT Id, Customer FROM Sales WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Customer", ColumnType.String),
        new ReportColumn("Amount", ColumnType.Decimal),
        new ReportColumn("Date", ColumnType.DateTime),
    });

    private static Dictionary<string, object?> Properties(string sql = Sql) => new() { ["sql"] = sql };

    [Fact]
    public void No_filters_returns_the_original_sql_unchanged()
    {
        var translator = new AdoFilterTranslator("postgres");

        var ok = translator.TryTranslate(Properties(), Array.Empty<PreviewFilter>(), Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeTrue();
        propertyOverrides["sql"].ShouldBe(Sql);
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Missing_sql_property_throws_configuration_exception()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        Should.Throw<ConfigurationException>(() => translator.TryTranslate(new Dictionary<string, object?>(), filters, Schema, out _, out _));
    }

    [Fact]
    public void Single_equals_filter_wraps_the_query_and_binds_one_parameter()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer = @filter0");
        parameters["filter0"].ShouldBe("Acme");
    }

    [Fact]
    public void Multiple_filters_are_joined_with_and_and_each_get_their_own_parameter()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[]
        {
            new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "100"),
            new PreviewFilter("Customer", PreviewFilterOperator.NotEquals, "Globex"),
        };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Amount > @filter0 AND t.Customer <> @filter1");
        parameters["filter0"].ShouldBe("100");
        parameters["filter1"].ShouldBe("Globex");
    }

    [Theory]
    [InlineData(PreviewFilterOperator.Equals, "=")]
    [InlineData(PreviewFilterOperator.NotEquals, "<>")]
    [InlineData(PreviewFilterOperator.GreaterThan, ">")]
    [InlineData(PreviewFilterOperator.GreaterThanOrEqual, ">=")]
    [InlineData(PreviewFilterOperator.LessThan, "<")]
    [InlineData(PreviewFilterOperator.LessThanOrEqual, "<=")]
    public void Comparison_operators_map_to_the_expected_sql_operator(PreviewFilterOperator op, string expectedSqlOperator)
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Amount", op, "42") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Amount {expectedSqlOperator} @filter0");
    }

    [Fact]
    public void Contains_uses_like_with_wildcards_in_the_bound_value_not_the_sql_text()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Contains, "cme") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer LIKE @filter0");
        parameters["filter0"].ShouldBe("%cme%");
    }

    [Fact]
    public void StartsWith_uses_like_with_a_trailing_wildcard_in_the_bound_value()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.StartsWith, "Ac") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer LIKE @filter0");
        parameters["filter0"].ShouldBe("Ac%");
    }

    [Fact]
    public void Oracle_prefix_uses_colon_bind_variables_instead_of_at()
    {
        var translator = new AdoFilterTranslator("oracle", parameterPrefix: ":");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer = :filter0");
        parameters["filter0"].ShouldBe("Acme");
    }

    [Fact]
    public void Type_reflects_the_constructor_argument()
    {
        new AdoFilterTranslator("mysql").Type.ShouldBe("mysql");
        new AdoFilterTranslator("oracle", ":").Type.ShouldBe("oracle");
    }

    [Fact]
    public void Unknown_operator_throws()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Customer", (PreviewFilterOperator)999, "Acme") };

        Should.Throw<ArgumentOutOfRangeException>(() => translator.TryTranslate(Properties(), filters, Schema, out _, out _));
    }

    [Theory]
    [InlineData(ColumnType.Integer, "bigint")]
    [InlineData(ColumnType.Decimal, "numeric")]
    [InlineData(ColumnType.Money, "numeric")]
    [InlineData(ColumnType.Boolean, "boolean")]
    [InlineData(ColumnType.Date, "date")]
    [InlineData(ColumnType.Time, "time")]
    [InlineData(ColumnType.DateTime, "timestamp")]
    [InlineData(ColumnType.Timestamp, "timestamp")]
    [InlineData(ColumnType.Uuid, "uuid")]
    public void PostgresCast_maps_every_castable_column_type(ColumnType type, string sqlType) =>
        AdoFilterTranslator.PostgresCast(type, "@filter0").ShouldBe($"@filter0::{sqlType}");

    [Theory]
    [InlineData(ColumnType.String)]
    [InlineData(ColumnType.Json)]
    [InlineData(ColumnType.Binary)]
    public void PostgresCast_returns_null_for_types_with_no_safe_cast(ColumnType type) =>
        AdoFilterTranslator.PostgresCast(type, "@filter0").ShouldBeNull();

    [Theory]
    [InlineData(ColumnType.Integer)]
    [InlineData(ColumnType.Decimal)]
    [InlineData(ColumnType.Money)]
    public void OracleCast_casts_numeric_types(ColumnType type) =>
        AdoFilterTranslator.OracleCast(type, ":filter0").ShouldBe(
            "TO_NUMBER(:filter0, 'FM999999999999999990.099999999999999999', 'NLS_NUMERIC_CHARACTERS=''.,''')");

    [Theory]
    [InlineData(ColumnType.String)]
    [InlineData(ColumnType.Boolean)]
    [InlineData(ColumnType.Uuid)]
    [InlineData(ColumnType.Date)]
    [InlineData(ColumnType.DateTime)]
    [InlineData(ColumnType.Timestamp)]
    public void OracleCast_returns_null_for_non_numeric_types(ColumnType type) =>
        AdoFilterTranslator.OracleCast(type, ":filter0").ShouldBeNull();

    [Fact]
    public void No_cast_configured_leaves_the_parameter_token_bare()
    {
        // The default (no castParameter) is what MySQL/SQL Server register with — those dialects
        // implicitly convert a text-bound parameter across a comparison reliably.
        var translator = new AdoFilterTranslator("mysql");
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "100.00") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Amount > @filter0");
    }

    [Theory]
    [InlineData("Amount", "numeric")]
    [InlineData("Date", "timestamp")]
    public void Postgres_registration_casts_the_bind_parameter_to_the_filtered_columns_type(string column, string sqlType)
    {
        var translator = new AdoFilterTranslator("postgres", castParameter: AdoFilterTranslator.PostgresCast);
        var filters = new[] { new PreviewFilter(column, PreviewFilterOperator.GreaterThan, "100") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.{column} > @filter0::{sqlType}");
    }

    [Fact]
    public void Postgres_registration_does_not_cast_a_string_column()
    {
        var translator = new AdoFilterTranslator("postgres", castParameter: AdoFilterTranslator.PostgresCast);
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer = @filter0");
    }

    [Theory]
    [InlineData(PreviewFilterOperator.Contains)]
    [InlineData(PreviewFilterOperator.StartsWith)]
    public void Contains_and_startswith_are_declined_against_a_non_string_column(PreviewFilterOperator op)
    {
        // A LIKE comparison only makes sense against text; against a numeric/date column it would
        // otherwise emit an uncastable `t.Amount LIKE @filter0` that every dialect rejects at
        // execution time — declining to translate here surfaces an honest 400 instead.
        var translator = new AdoFilterTranslator("postgres", castParameter: AdoFilterTranslator.PostgresCast);
        var filters = new[] { new PreviewFilter("Amount", op, "100") };

        var ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides["sql"].ShouldBe(Sql);
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Oracle_registration_casts_a_numeric_column_with_an_nls_independent_format()
    {
        var translator = new AdoFilterTranslator("oracle", parameterPrefix: ":", castParameter: AdoFilterTranslator.OracleCast);
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "2000.00") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _);

        propertyOverrides["sql"].ShouldBe(
            $"SELECT * FROM ({Sql}) t WHERE t.Amount > " +
            "TO_NUMBER(:filter0, 'FM999999999999999990.099999999999999999', 'NLS_NUMERIC_CHARACTERS=''.,''')");
    }

    [Fact]
    public void Oracle_registration_does_not_cast_a_date_column_yet()
    {
        // Date/DateTime/Timestamp casting for Oracle is a separate, still-open gap (blocked on a
        // reserved-word column-quoting issue) — left uncast rather than shipping an unverified fix.
        var translator = new AdoFilterTranslator("oracle", parameterPrefix: ":", castParameter: AdoFilterTranslator.OracleCast);
        var filters = new[] { new PreviewFilter("Date", PreviewFilterOperator.Equals, "2026-01-01") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Date = :filter0");
    }

    [Fact]
    public void Sql_registration_appends_offset_zero_rows_so_the_derived_table_stays_valid_t_sql()
    {
        // A derived table containing a bare ORDER BY (every keyset query already ends with one) is
        // invalid T-SQL unless followed by TOP, OFFSET, or FOR XML.
        var translator = new AdoFilterTranslator("sql", innerQuerySuffix: " OFFSET 0 ROWS");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _);

        propertyOverrides["sql"].ShouldBe($"SELECT * FROM ({Sql} OFFSET 0 ROWS) t WHERE t.Customer = @filter0");
    }
}
