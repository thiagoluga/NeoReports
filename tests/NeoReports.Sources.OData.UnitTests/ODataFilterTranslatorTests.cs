using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.OData.UnitTests;

/// <summary>
/// ADR D62: <see cref="ODataFilterTranslator"/> — the closed <see cref="PreviewFilterOperator"/>
/// set mapped onto OData v4 <c>$filter</c> syntax. Pure string/dictionary logic, no HTTP involved.
/// </summary>
public sealed class ODataFilterTranslatorTests
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

    private static Dictionary<string, object?> Properties(string? filter = null) =>
        filter is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["filter"] = filter };

    [Theory]
    [InlineData(PreviewFilterOperator.Equals, "eq")]
    [InlineData(PreviewFilterOperator.NotEquals, "ne")]
    [InlineData(PreviewFilterOperator.GreaterThan, "gt")]
    [InlineData(PreviewFilterOperator.GreaterThanOrEqual, "ge")]
    [InlineData(PreviewFilterOperator.LessThan, "lt")]
    [InlineData(PreviewFilterOperator.LessThanOrEqual, "le")]
    public void Comparison_operators_map_to_the_expected_odata_operator(PreviewFilterOperator op, string expectedODataOperator)
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", op, "100") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeTrue();
        propertyOverrides["filter"].ShouldBe($"Amount {expectedODataOperator} 100");
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Contains_maps_to_the_contains_function()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Contains, "cme") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("contains(Customer,'cme')");
    }

    [Fact]
    public void StartsWith_maps_to_the_startswith_function()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.StartsWith, "Ac") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("startswith(Customer,'Ac')");
    }

    [Theory]
    [InlineData(PreviewFilterOperator.Contains)]
    [InlineData(PreviewFilterOperator.StartsWith)]
    public void Contains_and_startswith_are_declined_against_a_non_string_column(PreviewFilterOperator op)
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", op, "100") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("filter").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void String_literal_doubles_an_embedded_single_quote()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "O'Brien") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("Customer eq 'O''Brien'");
    }

    [Fact]
    public void Uuid_literal_is_emitted_unquoted_per_odata_v4_edm_guid_syntax()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("ExternalId", PreviewFilterOperator.Equals, "3fa85f64-5717-4562-b3fc-2c963f66afa6") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("ExternalId eq 3fa85f64-5717-4562-b3fc-2c963f66afa6");
    }

    [Fact]
    public void Non_parseable_uuid_value_declines()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("ExternalId", PreviewFilterOperator.Equals, "not-a-guid") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("filter").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Boolean_literal_is_emitted_lowercase()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Active", PreviewFilterOperator.Equals, "True") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("Active eq true");
    }

    [Fact]
    public void Non_parseable_boolean_value_declines()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Active", PreviewFilterOperator.Equals, "yes") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("filter").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void DateTime_literal_is_emitted_as_a_bare_iso8601_value()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Created", PreviewFilterOperator.GreaterThan, "2026-01-01T00:00:00Z") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        var filterText = (string)propertyOverrides["filter"]!;
        filterText.ShouldStartWith("Created gt 2026-01-01T00:00:00");
        filterText.ShouldNotContain("'");
    }

    [Fact]
    public void Non_parseable_date_value_declines()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Created", PreviewFilterOperator.Equals, "not-a-date") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("filter").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Non_parseable_numeric_value_against_an_integer_column_declines()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Id", PreviewFilterOperator.Equals, "not-a-number") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("filter").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Non_parseable_decimal_value_declines()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "not-a-decimal") };

        bool ok = translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeFalse();
        propertyOverrides.ContainsKey("filter").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Decimal_literal_with_a_thousands_separator_is_reformatted_without_the_comma()
    {
        // A bare comma is not valid OData v4 numeric-literal syntax (it's the function-argument
        // separator) — NumberStyles.Number accepts "1,234.56" as a valid decimal, so the emitted
        // literal must be the reformatted parsed value, not the original text, or the generated
        // $filter would be malformed.
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", PreviewFilterOperator.Equals, "1,234.56") };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("Amount eq 1234.56");
    }

    [Fact]
    public void Multiple_filters_are_and_joined()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[]
        {
            new PreviewFilter("Amount", PreviewFilterOperator.GreaterThan, "100"),
            new PreviewFilter("Customer", PreviewFilterOperator.NotEquals, "Globex"),
        };

        translator.TryTranslate(Properties(), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("Amount gt 100 and Customer ne 'Globex'");
    }

    [Fact]
    public void Existing_static_filter_is_anded_with_the_generated_expression()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Customer", PreviewFilterOperator.Equals, "Acme") };

        translator.TryTranslate(Properties("Region eq 'US'"), filters, Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("(Region eq 'US') and (Customer eq 'Acme')");
    }

    [Fact]
    public void No_filters_with_no_existing_filter_returns_no_filter_override()
    {
        var translator = new ODataFilterTranslator();

        bool ok = translator.TryTranslate(Properties(), Array.Empty<PreviewFilter>(), Schema, out var propertyOverrides, out var parameters);

        ok.ShouldBeTrue();
        propertyOverrides.ContainsKey("filter").ShouldBeFalse();
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void No_filters_with_an_existing_filter_returns_it_unchanged()
    {
        var translator = new ODataFilterTranslator();

        translator.TryTranslate(Properties("Region eq 'US'"), Array.Empty<PreviewFilter>(), Schema, out var propertyOverrides, out _).ShouldBeTrue();

        propertyOverrides["filter"].ShouldBe("Region eq 'US'");
    }

    [Fact]
    public void Type_is_odata() => new ODataFilterTranslator().Type.ShouldBe("odata");

    [Fact]
    public void Unknown_operator_throws()
    {
        var translator = new ODataFilterTranslator();
        var filters = new[] { new PreviewFilter("Amount", (PreviewFilterOperator)999, "1") };

        Should.Throw<ArgumentOutOfRangeException>(() => translator.TryTranslate(Properties(), filters, Schema, out _, out _));
    }
}
