using NeoReports.Core.Preview;
using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>
/// G5 (ADR D45): <see cref="AdoFilterTranslator"/> translation — pure string/dictionary logic,
/// shared by every relational provider, so it lives in <c>NeoReports.Sources.Common</c> with no
/// database dependency. Assertions target the built <c>translatedSql</c> text and parameter
/// dictionary directly (never string-concatenated values).
/// </summary>
public class AdoFilterTranslatorTests
{
    private const string Sql = "SELECT Id, Customer FROM Sales WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    [Fact]
    public void No_filters_returns_the_original_sql_unchanged()
    {
        var translator = new AdoFilterTranslator("postgres");

        var ok = translator.TryTranslate(Sql, Array.Empty<PreviewFilter>(), out var translatedSql, out var parameters);

        ok.ShouldBeTrue();
        translatedSql.ShouldBe(Sql);
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Single_equals_filter_wraps_the_query_and_binds_one_parameter()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        translator.TryTranslate(Sql, filters, out var translatedSql, out var parameters);

        translatedSql.ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer = @filter0");
        parameters["filter0"].ShouldBe("Acme");
    }

    [Fact]
    public void Multiple_filters_are_joined_with_and_and_each_get_their_own_parameter()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[]
        {
            new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, 100m),
            new PreviewFilter("Customer", PreviewFilterOperator.NotEquals, "Globex"),
        };

        translator.TryTranslate(Sql, filters, out var translatedSql, out var parameters);

        translatedSql.ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Amount > @filter0 AND t.Customer <> @filter1");
        parameters["filter0"].ShouldBe(100m);
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
        var filters = new[] { new PreviewFilter("Amount", op, 42) };

        translator.TryTranslate(Sql, filters, out var translatedSql, out _);

        translatedSql.ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Amount {expectedSqlOperator} @filter0");
    }

    [Fact]
    public void Contains_uses_like_with_wildcards_in_the_bound_value_not_the_sql_text()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Contains, "cme") };

        translator.TryTranslate(Sql, filters, out var translatedSql, out var parameters);

        translatedSql.ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer LIKE @filter0");
        parameters["filter0"].ShouldBe("%cme%");
    }

    [Fact]
    public void StartsWith_uses_like_with_a_trailing_wildcard_in_the_bound_value()
    {
        var translator = new AdoFilterTranslator("postgres");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.StartsWith, "Ac") };

        translator.TryTranslate(Sql, filters, out var translatedSql, out var parameters);

        translatedSql.ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer LIKE @filter0");
        parameters["filter0"].ShouldBe("Ac%");
    }

    [Fact]
    public void Oracle_prefix_uses_colon_bind_variables_instead_of_at()
    {
        var translator = new AdoFilterTranslator("oracle", parameterPrefix: ":");
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        translator.TryTranslate(Sql, filters, out var translatedSql, out var parameters);

        translatedSql.ShouldBe($"SELECT * FROM ({Sql}) t WHERE t.Customer = :filter0");
        parameters["filter0"].ShouldBe("Acme");
    }

    [Fact]
    public void Type_reflects_the_constructor_argument()
    {
        new AdoFilterTranslator("mysql").Type.ShouldBe("mysql");
        new AdoFilterTranslator("oracle", ":").Type.ShouldBe("oracle");
    }
}
