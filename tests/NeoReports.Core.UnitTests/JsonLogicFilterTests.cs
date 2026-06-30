using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Epic A / A4: the JsonLogic filter compiles a JSON expression into a predicate over a positional
/// <see cref="ReportRecord"/>. <c>{"var": "Name"}</c> reads a column by name.
/// </summary>
public class JsonLogicFilterTests
{
    private static readonly ReportSchema Schema = new(new ReportColumn[]
    {
        new("Id", ColumnType.Integer),
        new("Customer", ColumnType.String),
        new("Amount", ColumnType.Decimal),
    });

    private static ReportRecord Rec(long id, string customer, decimal amount) =>
        new(Schema, new object?[] { id, customer, amount });

    private static bool Eval(string expression, ReportRecord record) =>
        JsonLogicFilter.Compile(expression).Invoke(record);

    [Theory]
    [InlineData("""{ "==": [ { "var": "Id" }, 1 ] }""", true)]
    [InlineData("""{ "==": [ { "var": "Id" }, 2 ] }""", false)]
    [InlineData("""{ "!=": [ { "var": "Customer" }, "Acme" ] }""", false)]
    [InlineData("""{ "===": [ { "var": "Customer" }, "Acme" ] }""", true)]
    [InlineData("""{ ">": [ { "var": "Amount" }, 100 ] }""", true)]
    [InlineData("""{ ">=": [ { "var": "Amount" }, 150 ] }""", true)]
    [InlineData("""{ "<": [ { "var": "Amount" }, 150 ] }""", false)]
    [InlineData("""{ "<=": [ { "var": "Amount" }, 149.99 ] }""", false)]
    public void Evaluates_comparisons(string expression, bool expected) =>
        Eval(expression, Rec(1, "Acme", 150m)).ShouldBe(expected);

    [Fact]
    public void Evaluates_and_or_not()
    {
        var record = Rec(1, "Acme", 150m);

        Eval("""{ "and": [ { ">": [ { "var": "Amount" }, 0 ] }, { "==": [ { "var": "Customer" }, "Acme" ] } ] }""", record)
            .ShouldBeTrue();
        Eval("""{ "and": [ { ">": [ { "var": "Amount" }, 0 ] }, { "==": [ { "var": "Customer" }, "Globex" ] } ] }""", record)
            .ShouldBeFalse();
        Eval("""{ "or": [ { "<": [ { "var": "Amount" }, 0 ] }, { "==": [ { "var": "Id" }, 1 ] } ] }""", record)
            .ShouldBeTrue();
        Eval("""{ "!": [ { "==": [ { "var": "Id" }, 2 ] } ] }""", record).ShouldBeTrue();
    }

    [Fact]
    public void Evaluates_in_for_arrays_and_strings()
    {
        var record = Rec(1, "Acme", 150m);

        Eval("""{ "in": [ { "var": "Id" }, [ 1, 2, 3 ] ] }""", record).ShouldBeTrue();
        Eval("""{ "in": [ { "var": "Id" }, [ 4, 5 ] ] }""", record).ShouldBeFalse();
        Eval("""{ "in": [ "cm", { "var": "Customer" } ] }""", record).ShouldBeTrue(); // "cm" in "Acme"
    }

    [Fact]
    public void Var_uses_a_default_when_the_column_is_absent()
    {
        var record = Rec(1, "Acme", 150m);
        Eval("""{ "==": [ { "var": [ "Missing", 7 ] }, 7 ] }""", record).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{ "pow": [ 2, 3 ] }""")]   // unsupported operator
    [InlineData("not json")]                    // malformed
    [InlineData("")]                            // empty
    [InlineData("""{ "==": [ 1 ] }""")]         // wrong arity
    public void Rejects_unsupported_or_malformed_expressions(string expression) =>
        Should.Throw<ConfigurationException>(() => JsonLogicFilter.Compile(expression));
}
