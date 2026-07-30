using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Registry;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class ConfigStartupValidationTests
{
    private static string ConfigNamed(string name) => $$"""
    {
      "name": "{{name}}",
      "source": { "type": "inmemory" },
      "columns": [ { "name": "Id", "type": "Integer" } ],
      "outputs": [ { "format": "csv" } ],
      "destinations": [ { "type": "capture" } ]
    }
    """;

    [Fact]
    public void Registers_the_startup_validation_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeoReportsStartupValidation();

        services.ShouldContain(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(StartupValidationHostedService));
    }

    [Fact]
    public async Task Compiles_a_valid_config_at_startup_without_a_request()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReportFromConfig(ConfigNamed("sales"));
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>()));
        services.AddSingleton<IWriterFactory>(new FakeWriterFactory("csv", "csv"));
        services.AddSingleton<IDestinationFactory>(new CapturingDestinationFactory());
        services.AddNeoReportsStartupValidation();
        await using var provider = services.BuildServiceProvider();

        StartupValidationHostedService hosted = provider.GetServices<IHostedService>()
            .OfType<StartupValidationHostedService>().Single();

        await Should.NotThrowAsync(() => hosted.StartAsync(CancellationToken.None));
        provider.GetRequiredService<IReportRegistry>().Find("sales").ShouldNotBeNull();
    }

    [Fact]
    public void Fails_fast_at_startup_on_a_malformed_config()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // References the "inmemory" source, but no matching IConfigSourceProvider is registered — the
        // config only fails to compile when the registry is resolved, which the hosted service forces.
        services.AddReportFromConfig(ConfigNamed("broken"));
        services.AddNeoReportsStartupValidation();
        using var provider = services.BuildServiceProvider();

        // Constructing the hosted services injects IReportRegistry; its resolution compiles the config
        // and throws — the host would fail to start rather than surfacing this on the first request.
        Should.Throw<ConfigurationException>(
            () => provider.GetServices<IHostedService>().OfType<StartupValidationHostedService>().ToList());
    }
}
