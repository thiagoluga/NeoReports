using System.Text.Json;
using NeoReports.AspNetCore;
using Shouldly;
using Xunit;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// G7: <see cref="FilterValueConverter"/> — pure JSON (de)serialization logic, no host required.
/// Every branch of <c>Read</c>/<c>Write</c> is covered directly since it's the seam that makes a
/// filter value always a literal string with no date-sniffing (see the class's own remarks).
/// </summary>
public class FilterValueConverterTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new FilterValueConverter() } };

    [Fact]
    public void Reads_null()
    {
        JsonSerializer.Deserialize<string?>("null", Options).ShouldBeNull();
    }

    [Fact]
    public void Reads_a_string_verbatim()
    {
        JsonSerializer.Deserialize<string?>("\"12.25\"", Options).ShouldBe("12.25");
    }

    [Fact]
    public void Reads_true_as_the_literal_string_true()
    {
        JsonSerializer.Deserialize<string?>("true", Options).ShouldBe("true");
    }

    [Fact]
    public void Reads_false_as_the_literal_string_false()
    {
        JsonSerializer.Deserialize<string?>("false", Options).ShouldBe("false");
    }

    [Theory]
    [InlineData("2000", "2000")]
    [InlineData("2000.00", "2000.00")]
    [InlineData("-1", "-1")]
    [InlineData("0.10", "0.10")]
    public void Reads_a_number_as_its_exact_written_digits(string json, string expected)
    {
        JsonSerializer.Deserialize<string?>(json, Options).ShouldBe(expected);
    }

    [Fact]
    public void Reads_an_object_throws()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<string?>("{}", Options));
    }

    [Fact]
    public void Reads_an_array_throws()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<string?>("[]", Options));
    }

    [Fact]
    public void Writes_null()
    {
        JsonSerializer.Serialize<string?>(null, Options).ShouldBe("null");
    }

    [Fact]
    public void Writes_a_string()
    {
        JsonSerializer.Serialize<string?>("12.25", Options).ShouldBe("\"12.25\"");
    }
}
