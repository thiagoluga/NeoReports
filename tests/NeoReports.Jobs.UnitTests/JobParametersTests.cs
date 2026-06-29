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
        back["count"].ShouldBe(42L);
        back["ratio"].ShouldBe(1.5);
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
