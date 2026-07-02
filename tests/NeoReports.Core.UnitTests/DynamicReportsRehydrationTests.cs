using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Configuration;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Epic D / D1: <c>AddDynamicReports()</c> rehydrates every document in the config store on the
/// first resolution of <see cref="IReportRegistry"/>, mirroring the code-first
/// <c>AddReportFromConfig</c> lazy-compile mechanism.
/// </summary>
public class DynamicReportsRehydrationTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "nr-rehydrate-" + Guid.NewGuid().ToString("N"));

    private static string ConfigNamed(string name) => $$"""
    {
      "name": "{{name}}",
      "source": { "type": "inmemory" },
      "columns": [ { "name": "Id", "type": "Integer" } ],
      "outputs": [ { "format": "csv" } ]
    }
    """;

    private static void RegisterProviders(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>()));
        services.AddSingleton<IWriterFactory>(new FakeWriterFactory("csv", "csv"));
    }

    [Fact]
    public async Task Stored_config_is_registered_and_runnable_after_restart()
    {
        var store = new FileReportConfigStore(_directory);
        await store.SaveAsync("alpha", ConfigNamed("alpha"), CancellationToken.None);

        var services = new ServiceCollection();
        RegisterProviders(services);
        services.AddDynamicReports(o => o.Directory = _directory);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IReportRegistry>();

        registry.Contains("alpha").ShouldBeTrue();
    }

    [Fact]
    public async Task Corrupt_file_is_skipped_while_valid_sibling_still_loads()
    {
        var store = new FileReportConfigStore(_directory);
        await store.SaveAsync("good", ConfigNamed("good"), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Join(_directory, "bad.json"), "{ not valid json");

        var services = new ServiceCollection();
        RegisterProviders(services);
        services.AddDynamicReports(o => o.Directory = _directory);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IReportRegistry>();

        registry.Contains("good").ShouldBeTrue();
        registry.Contains("bad").ShouldBeFalse();
    }

    [Fact]
    public async Task Name_collision_with_code_first_report_keeps_the_code_first_one()
    {
        await new FileReportConfigStore(_directory).SaveAsync("dup", ConfigNamed("dup"), CancellationToken.None);

        var services = new ServiceCollection();
        RegisterProviders(services);
        var codeFirstSource = new FakeBatchSource<Sale>(new[] { new[] { new Sale(1, "A", 1m, DateTime.UnixEpoch) } });
        services.AddReport<Sale>("dup", b => b
            .From(codeFirstSource)
            .Column(v => v.Id, "Id")
            .Column(v => v.Customer, "Customer")
            .To(new OutputSpec(new FakeWriterFactory())));
        services.AddDynamicReports(o => o.Directory = _directory);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IReportRegistry>();
        var report = registry.Find("dup");

        report.ShouldNotBeNull();
        // The code-first report declares 2 columns (Id, Customer); the stored config declares 1 (Id).
        report.Schema.Columns.Count.ShouldBe(2);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
