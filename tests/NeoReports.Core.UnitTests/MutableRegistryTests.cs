using NeoReports.Core.Building;
using NeoReports.Core.Registry;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>Epic D / D1: <see cref="IMutableReportRegistry.Unregister"/> on <see cref="ReportRegistry"/>.</summary>
public class MutableRegistryTests
{
    private static CompiledReport BuildReport(string name)
    {
        var source = new FakeBatchSource<Sale>(new[] { new[] { new Sale(1, "A", 10m, DateTime.UnixEpoch) } });
        return new ReportBuilder<Sale>(name)
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();
    }

    [Fact]
    public void Unregister_removes_a_registered_report()
    {
        var registry = new ReportRegistry();
        registry.Register(BuildReport("r1"));

        registry.Unregister("r1").ShouldBeTrue();

        registry.Contains("r1").ShouldBeFalse();
        registry.Find("r1").ShouldBeNull();
    }

    [Fact]
    public void Unregister_unknown_name_returns_false()
    {
        var registry = new ReportRegistry();

        registry.Unregister("missing").ShouldBeFalse();
    }

    [Fact]
    public void Register_after_unregister_of_same_name_succeeds()
    {
        var registry = new ReportRegistry();
        registry.Register(BuildReport("r1"));
        registry.Unregister("r1").ShouldBeTrue();

        registry.Register(BuildReport("r1"));

        registry.Contains("r1").ShouldBeTrue();
    }

    [Fact]
    public void ReportRegistry_is_reachable_through_IMutableReportRegistry()
    {
        IMutableReportRegistry registry = new ReportRegistry();
        registry.Register(BuildReport("r1"));

        registry.Contains("r1").ShouldBeTrue();
        registry.Unregister("r1").ShouldBeTrue();
        registry.Contains("r1").ShouldBeFalse();
    }
}
