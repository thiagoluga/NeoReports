using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.SourceRegistry;

/// <summary>ADR D42: <c>AddSourceRegistry</c> / <c>AddInMemorySourceRegistry</c> DI registration.</summary>
public class SourceRegistryServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSourceRegistry_registers_a_resolvable_file_backed_store()
    {
        var dir = Path.Join(Path.GetTempPath(), "nr-d42-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new ServiceCollection()
                .AddSourceRegistry(o => o.Directory = dir)
                .BuildServiceProvider();

            provider.GetRequiredService<ISourceRegistryStore>().ShouldBeOfType<FileSourceRegistryStore>();
            provider.GetRequiredService<ISourceRegistry>().ShouldBeOfType<SourceRegistryService>();
            provider.GetRequiredService<SourceRegistryOptions>().Directory.ShouldBe(dir);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddInMemorySourceRegistry_registers_a_resolvable_in_memory_store()
    {
        using var provider = new ServiceCollection()
            .AddInMemorySourceRegistry()
            .BuildServiceProvider();

        provider.GetRequiredService<ISourceRegistryStore>().ShouldBeOfType<InMemorySourceRegistryStore>();
        provider.GetRequiredService<ISourceRegistry>().ShouldBeOfType<SourceRegistryService>();
    }

    [Fact]
    public void No_registration_leaves_ISourceRegistry_unresolvable()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        provider.GetService<ISourceRegistry>().ShouldBeNull();
    }
}
