using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// ADR D42, locked decision 4: the typed path's by-name sources (e.g. <c>Source.SqlNamed</c>,
/// represented here by <see cref="FakeNamedBatchSource{T}"/>) require a source registry at
/// registration time and receive the run's <see cref="IServiceProvider"/> once per run.
/// </summary>
public class NamedSourceResolutionTests
{
    private static readonly Sale[] OneSale = { new(1, "Acme", 10m, DateTime.UnixEpoch) };

    [Fact]
    public void AddReport_with_a_named_source_and_no_registry_throws_at_registration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var source = new FakeNamedBatchSource<Sale>("sales-db", new[] { OneSale });

        void Register() => services.AddReport<Sale>("sales", b => b
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory())));

        Should.Throw<ConfigurationException>(Register).Message.ShouldContain("sales");
    }

    [Fact]
    public void AddReport_with_a_named_source_succeeds_when_a_registry_is_configured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemorySourceRegistry();
        var source = new FakeNamedBatchSource<Sale>("sales-db", new[] { OneSale });

        services.AddReport<Sale>("sales", b => b
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory())));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IReportRegistry>();
        registry.Contains("sales").ShouldBeTrue();
    }

    [Fact]
    public void CompiledReport_SourceRef_is_populated_from_the_named_source()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemorySourceRegistry();
        var source = new FakeNamedBatchSource<Sale>("sales-db", new[] { OneSale });

        services.AddReport<Sale>("sales", b => b
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory())));

        using var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<IReportRegistry>().Find("sales");

        report.ShouldNotBeNull();
        report!.SourceRef.ShouldBe("sales-db");
    }

    [Fact]
    public async Task Running_the_report_attaches_the_run_services_to_the_named_source()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemorySourceRegistry();
        var source = new FakeNamedBatchSource<Sale>("sales-db", new[] { OneSale });

        services.AddReport<Sale>("sales", b => b
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory())));

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IReportRunner>();

        source.AttachServicesCalls.ShouldBe(0);
        ReportRunResult result = await runner.RunAsync("sales");

        result.Status.ShouldBe(ReportRunStatus.Completed);
        source.AttachServicesCalls.ShouldBeGreaterThanOrEqualTo(1);
        source.LastAttachedServices.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_second_run_re_attaches_services_rather_than_reusing_the_first_runs()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemorySourceRegistry();
        var source = new FakeNamedBatchSource<Sale>("sales-db", new[] { OneSale });

        services.AddReport<Sale>("sales", b => b
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory())));

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IReportRunner>();

        await runner.RunAsync("sales");
        var firstCalls = source.AttachServicesCalls;
        await runner.RunAsync("sales");

        source.AttachServicesCalls.ShouldBeGreaterThan(firstCalls);
    }
}
