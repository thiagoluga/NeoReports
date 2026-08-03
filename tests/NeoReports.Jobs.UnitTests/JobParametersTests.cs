using NeoReports.Jobs;
using Shouldly;
using Xunit;

namespace NeoReports.Jobs.UnitTests;

public class JobParametersTests
{
    [Fact]
    public void Round_trips_primitive_values()
    {
        var original = new Dictionary<string, object?>
        {
            ["name"] = "sales",
            ["count"] = 42L,
            ["ratio"] = 1.5,
            ["active"] = true,
            ["missing"] = null,
        };

        var json = JobParameters.Serialize(original);
        var back = JobParameters.Deserialize(json);

        back["name"].ShouldBe("sales");
        // ShouldBe alone can't police this: Shouldly compares numerics by value, so a double 42.0
        // satisfies ShouldBe(42L) and the whole-number-boxed-as-double bug slipped through. Assert
        // the runtime type — a provider binds an integer column by the CLR type it is handed, and a
        // double past 2^53 silently loses precision.
        back["count"].ShouldBeOfType<long>().ShouldBe(42L);
        back["ratio"].ShouldBeOfType<double>().ShouldBe(1.5);
        back["active"].ShouldBe(true);
        back["missing"].ShouldBeNull();
    }

    [Fact]
    public void Round_trips_datetime_as_datetime()
    {
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var json = JobParameters.Serialize(new Dictionary<string, object?> { ["start"] = date });

        var back = JobParameters.Deserialize(json);

        back["start"].ShouldBeOfType<DateTime>();
        ((DateTime)back["start"]!).ShouldBe(date);
    }

    [Fact]
    public void Null_or_empty_json_yields_empty_dictionary()
    {
        JobParameters.Deserialize(null).ShouldBeEmpty();
        JobParameters.Deserialize("").ShouldBeEmpty();
    }

    [Fact]
    public void Serialize_null_parameters_yields_empty_object()
    {
        JobParameters.Serialize(null).ShouldBe("{}");
    }
}
