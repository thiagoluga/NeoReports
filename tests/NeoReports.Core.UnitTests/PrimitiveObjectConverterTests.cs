using System.Text.Json;
using NeoReports.Core.Configuration;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// <see cref="PrimitiveObjectConverter"/> reads a JSON number as the narrowest CLR type that
/// round-trips it exactly (<c>long</c> for a whole number, <c>double</c> otherwise) — the property
/// bag's own doc comment promises this, and callers (e.g. the sample providers' <c>raw is long n</c>
/// row-count check) rely on it. Assertions use <c>ShouldBeOfType</c>, not <c>ShouldBe</c>: Shouldly's
/// <c>ShouldBe</c> coerces across numeric types for comparison, so a boxed <c>double</c> 10.0 passes
/// a <c>ShouldBe(10L)</c> assertion just as happily as a boxed <c>long</c> 10 would — exactly the gap
/// that let a real bug (the switch expression below implicitly widening every successfully-parsed
/// <c>long</c> to <c>double</c> to unify with the other arm's type) ship undetected.
/// </summary>
public class PrimitiveObjectConverterTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new PrimitiveObjectConverter());
        return options;
    }

    private static object? Read(string json) => JsonSerializer.Deserialize<object?>(json, Options);

    [Fact]
    public void Whole_number_deserializes_as_long_not_double()
    {
        object? value = Read("5");

        value.ShouldBeOfType<long>();
        value.ShouldBe(5L);
    }

    [Fact]
    public void Fractional_number_deserializes_as_double()
    {
        object? value = Read("1.5");

        value.ShouldBeOfType<double>();
        value.ShouldBe(1.5);
    }

    [Fact]
    public void Negative_whole_number_deserializes_as_long()
    {
        object? value = Read("-42");

        value.ShouldBeOfType<long>();
        value.ShouldBe(-42L);
    }
}
