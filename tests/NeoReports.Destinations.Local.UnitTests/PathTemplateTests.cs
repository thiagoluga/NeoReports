using NeoReports.Destinations.Local;
using Shouldly;
using Xunit;

namespace NeoReports.Destinations.Local.UnitTests;

public class PathTemplateTests
{
    private static readonly DateTimeOffset Ts = new(2026, 3, 7, 13, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Expands_name_ext_and_default_date()
    {
        var result = PathTemplate.Expand("{name}-{date}.{ext}", "vendas", "csv", Ts);
        result.ShouldBe("vendas-2026-03-07.csv");
    }

    [Fact]
    public void Expands_date_with_custom_format()
    {
        var result = PathTemplate.Expand("out/{name}_{date:yyyyMM}.{ext}", "rel", "xlsx", Ts);
        result.ShouldBe("out/rel_202603.xlsx");
    }

    [Fact]
    public void Expands_parameter_tokens()
    {
        var parameters = new Dictionary<string, object?> { ["regiao"] = "sul" };
        var result = PathTemplate.Expand("{name}-{regiao}.{ext}", "rel", "csv", Ts, parameters);
        result.ShouldBe("rel-sul.csv");
    }

    [Fact]
    public void Leaves_unknown_tokens_untouched()
    {
        var result = PathTemplate.Expand("{name}-{missing}.{ext}", "rel", "csv", Ts);
        result.ShouldBe("rel-{missing}.csv");
    }
}
