using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Configuration;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Scheduling;

/// <summary>ADR D41: <c>ReportBuilder&lt;T&gt;.Schedule</c>, <c>CompiledReport.Schedule</c>, and the dynamic-path compiler.</summary>
public class ScheduleConfigTests
{
    private static ReportBuilder<Sale> BaseBuilder() =>
        new ReportBuilder<Sale>("r")
            .From(new FakeBatchSource<Sale>(Array.Empty<IReadOnlyList<Sale>>()))
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()));

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>()));
        services.AddSingleton<IWriterFactory>(new FakeWriterFactory("fake", "fake"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Schedule_sets_CompiledReport_Schedule()
    {
        CompiledReport report = BaseBuilder().Schedule("0 6 * * 1").Build();
        report.Schedule.ShouldBe(new ScheduleConfig("0 6 * * 1"));
    }

    [Fact]
    public void No_Schedule_call_leaves_CompiledReport_Schedule_null()
    {
        CompiledReport report = BaseBuilder().Build();
        report.Schedule.ShouldBeNull();
    }

    [Fact]
    public void Schedule_rejects_an_invalid_cron_expression()
    {
        var ex = Should.Throw<ConfigurationException>(() => BaseBuilder().Schedule("not a cron"));
        ex.Message.ShouldContain("not a cron");
    }

    private static ReportConfig ConfigWithSchedule(ScheduleConfig? schedule) => new(
        Name: "r",
        Source: new SourceConfig("inmemory"),
        Columns: new[] { new ColumnConfig("Id", ColumnType.Integer) },
        Outputs: new[] { new OutputConfig("fake") },
        Schedule: schedule);

    [Fact]
    public void Compiler_applies_a_valid_schedule()
    {
        var services = BuildServices();
        CompiledReport report = ReportConfigCompiler.Compile(ConfigWithSchedule(new ScheduleConfig("*/5 * * * *")), services);
        report.Schedule.ShouldBe(new ScheduleConfig("*/5 * * * *"));
    }

    [Fact]
    public void Compiler_leaves_Schedule_null_when_not_configured()
    {
        var services = BuildServices();
        CompiledReport report = ReportConfigCompiler.Compile(ConfigWithSchedule(null), services);
        report.Schedule.ShouldBeNull();
    }

    [Fact]
    public void Compiler_rejects_an_invalid_cron_expression()
    {
        var services = BuildServices();
        var ex = Should.Throw<ConfigurationException>(
            () => ReportConfigCompiler.Compile(ConfigWithSchedule(new ScheduleConfig("garbage")), services));
        ex.Message.ShouldContain("garbage");
    }
}
