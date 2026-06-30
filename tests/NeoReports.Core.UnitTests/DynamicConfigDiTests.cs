using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Epic A / A5: config-driven reports registered through DI (<c>AddReportFromConfig</c> /
/// <c>AddReportsFromConfigDirectory</c>) are compiled lazily when the registry is first resolved and
/// are runnable by name through the standard runner — exactly like code-first reports.
/// </summary>
public class DynamicConfigDiTests
{
    private static string ConfigNamed(string name) => $$"""
    {
      "name": "{{name}}",
      "source": { "type": "inmemory" },
      "columns": [
        { "name": "Id", "type": "Integer" },
        { "name": "Customer", "type": "String" }
      ],
      "outputs": [ { "format": "csv" } ],
      "destinations": [ { "type": "capture" } ]
    }
    """;

    [Fact]
    public async Task Registers_a_config_report_and_runs_it_by_name()
    {
        var destination = new CapturingDestinationFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReportFromConfig(ConfigNamed("sales"));
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
            new[] { new object?[] { 1L, "Acme" }, new object?[] { 2L, "Globex" } }));
        services.AddSingleton<IWriterFactory>(new FakeWriterFactory("csv", "csv"));
        services.AddSingleton<IDestinationFactory>(destination);
        await using var provider = services.BuildServiceProvider();

        var runner = provider.GetRequiredService<IReportRunner>();
        ReportRunResult result = await runner.RunAsync("sales");

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.RecordsWritten.ShouldBe(2);
        Encoding.UTF8.GetString(destination.LastDestination!.Files["sales.csv"]).ShouldContain("Globex");
    }

    [Fact]
    public void Loads_every_config_from_a_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nr-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "a.json"), ConfigNamed("alpha"));
            File.WriteAllText(Path.Combine(directory, "b.json"), ConfigNamed("beta"));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddReportsFromConfigDirectory(directory);
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>()));
            services.AddSingleton<IWriterFactory>(new FakeWriterFactory("csv", "csv"));
            services.AddSingleton<IDestinationFactory>(new CapturingDestinationFactory());
            using var provider = services.BuildServiceProvider();

            var registry = provider.GetRequiredService<IReportRegistry>();
            registry.Names.ShouldBe(new[] { "alpha", "beta" }, ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Resolving_the_registry_surfaces_a_missing_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReportFromConfig(ConfigNamed("x")); // references the "inmemory" source, which is not registered
        using var provider = services.BuildServiceProvider();

        Should.Throw<ConfigurationException>(() => provider.GetRequiredService<IReportRegistry>());
    }
}
