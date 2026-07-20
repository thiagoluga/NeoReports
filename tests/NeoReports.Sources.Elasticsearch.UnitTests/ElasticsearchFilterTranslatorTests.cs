using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Elasticsearch.UnitTests;

/// <summary>
/// ADR D64: <see cref="ElasticsearchFilterTranslator"/> — the closed <see cref="PreviewFilterOperator"/>
/// set mapped onto Elasticsearch Query DSL, built as a JSON node tree (never string-concatenated).
/// </summary>
public sealed class ElasticsearchFilterTranslatorTests
{
    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Customer", ColumnType.String),
        new ReportColumn("Amount", ColumnType.Decimal),
        new ReportColumn("Active", ColumnType.Boolean),
        new ReportColumn("Created", ColumnType.DateTime),
        new ReportColumn("ExternalId", ColumnType.Uuid),
    });

    private static Dictionary<string, object?> Properties(string? query = null) =>
        query is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["query"] = JsonDocument.Parse(query).RootElement.Clone() };

    private static JsonElement Query(IReadOnlyDictionary<string, object?> propertyOverrides) => (JsonElement)propertyOverrides["query"]!;

    [Fact]
    public void Equals_maps_to_a_term_query()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.Equals, "100") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters).ShouldBeTrue();

        Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0].GetProperty("term").GetProperty("Amount").GetDecimal().ShouldBe(100m);
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void NotEquals_maps_to_a_bool_must_not_term_query()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.NotEquals, "Acme") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        JsonElement clause = Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0];
        clause.GetProperty("bool").GetProperty("must_not")[0].GetProperty("term").GetProperty("Customer").GetString().ShouldBe("Acme");
    }

    [Theory]
    [InlineData(PreviewFilterOperator.GreaterThan, "gt")]
    [InlineData(PreviewFilterOperator.GreaterThanOrEqual, "gte")]
    [InlineData(PreviewFilterOperator.LessThan, "lt")]
    [InlineData(PreviewFilterOperator.LessThanOrEqual, "lte")]
    public void Comparison_operators_map_to_a_range_query(PreviewFilterOperator op, string expectedRangeOp)
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", op, "100") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0]
            .GetProperty("range").GetProperty("Amount").GetProperty(expectedRangeOp).GetDecimal().ShouldBe(100m);
    }

    [Fact]
    public void StartsWith_maps_to_a_prefix_query()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.StartsWith, "Ac") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0]
            .GetProperty("prefix").GetProperty("Customer").GetString().ShouldBe("Ac");
    }

    [Fact]
    public void Contains_maps_to_a_wildcard_query_wrapped_in_asterisks()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Contains, "cme") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0]
            .GetProperty("wildcard").GetProperty("Customer").GetString().ShouldBe("*cme*");
    }

    [Fact]
    public void Contains_escapes_wildcard_metacharacters_in_the_value()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Contains, "50%*off?") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        string wildcard = Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0]
            .GetProperty("wildcard").GetProperty("Customer").GetString()!;
        wildcard.ShouldBe("*50%\\*off\\?*");
    }

    [Theory]
    [InlineData(PreviewFilterOperator.Contains)]
    [InlineData(PreviewFilterOperator.StartsWith)]
    public void Contains_and_startswith_are_declined_against_a_non_string_column(PreviewFilterOperator op)
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", op, "100") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("query").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Uuid_literal_is_a_json_string()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("ExternalId", PreviewFilterOperator.Equals, "3fa85f64-5717-4562-b3fc-2c963f66afa6") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0]
            .GetProperty("term").GetProperty("ExternalId").GetString().ShouldBe("3fa85f64-5717-4562-b3fc-2c963f66afa6");
    }

    [Fact]
    public void Boolean_literal_is_a_json_boolean()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Active", PreviewFilterOperator.Equals, "True") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0]
            .GetProperty("term").GetProperty("Active").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Non_parseable_boolean_value_declines()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Active", PreviewFilterOperator.Equals, "yes") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("query").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void DateTime_literal_is_emitted_as_an_iso8601_string()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Created", PreviewFilterOperator.GreaterThan, "2026-01-01T00:00:00Z") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        string literal = Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0]
            .GetProperty("range").GetProperty("Created").GetProperty("gt").GetString()!;
        literal.ShouldStartWith("2026-01-01T00:00:00");
    }

    [Fact]
    public void Offsetless_datetime_literal_is_assumed_utc_not_the_host_local_timezone()
    {
        // Regression: unlike DateTimeStyles.None (which would interpret an offset-less value in
        // whatever timezone the host process happens to run in), an offset-less filter value must be
        // deterministically anchored to UTC — Elasticsearch date fields are conventionally UTC, and a
        // host-timezone-dependent parse would silently shift the boundary differently per deployment.
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Created", PreviewFilterOperator.Equals, "2026-01-01T12:00:00") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        string literal = Query(propertyOverrides).GetProperty("bool").GetProperty("filter")[0]
            .GetProperty("term").GetProperty("Created").GetString()!;
        literal.ShouldBe("2026-01-01T12:00:00.0000000+00:00");
    }

    [Fact]
    public void Non_parseable_date_value_declines()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Created", PreviewFilterOperator.Equals, "not-a-date") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("query").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Non_parseable_decimal_value_declines()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "not-a-decimal") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("query").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Multiple_filters_are_anded_via_bool_filter_array()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[]
        {
            new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "100"),
            new PreviewFilter("Customer", PreviewFilterOperator.NotEquals, "Globex"),
        };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        Query(propertyOverrides).GetProperty("bool").GetProperty("filter").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void Existing_static_query_is_anded_with_the_generated_filters()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        translator.TryTranslate(Properties("""{"term":{"region":"US"}}"""), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        JsonElement merged = Query(propertyOverrides);
        JsonElement must = merged.GetProperty("bool").GetProperty("must");
        must.GetArrayLength().ShouldBe(2);
        must[0].GetProperty("term").GetProperty("region").GetString().ShouldBe("US");
        must[1].GetProperty("bool").GetProperty("filter")[0].GetProperty("term").GetProperty("Customer").GetString().ShouldBe("Acme");
    }

    [Fact]
    public void No_filters_with_no_existing_query_returns_no_query_override()
    {
        var translator = new ElasticsearchFilterTranslator();

        bool ok = translator.TryTranslate(Properties(), Array.Empty<PreviewFilter>(), Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeTrue();
        propertyOverrides.ContainsKey("query").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void No_filters_with_an_existing_query_returns_it_unchanged()
    {
        var translator = new ElasticsearchFilterTranslator();

        translator.TryTranslate(Properties("""{"term":{"region":"US"}}"""), Array.Empty<PreviewFilter>(), Schema, out var propertyOverrides, out _).ShouldBeTrue();

        Query(propertyOverrides).GetProperty("term").GetProperty("region").GetString().ShouldBe("US");
    }

    [Fact]
    public void Type_is_elasticsearch() => new ElasticsearchFilterTranslator().Type.ShouldBe("elasticsearch");

    [Fact]
    public void Unknown_operator_throws()
    {
        var translator = new ElasticsearchFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", (PreviewFilterOperator)999, "1") };

        Should.Throw<ArgumentOutOfRangeException>(() => translator.TryTranslate(Properties(), filters, Schema, out _, out _));
    }
}
