using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Events;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Events;

/// <summary>ADR D38: <c>AddJobEvents</c> / <c>AddInMemoryJobEvents</c> DI registration.</summary>
public class JobEventsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddJobEvents_registers_a_resolvable_file_backed_store()
    {
        var dir = Path.Join(Path.GetTempPath(), "nr-d38-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new ServiceCollection()
                .AddJobEvents(o => o.Directory = dir)
                .BuildServiceProvider();

            provider.GetRequiredService<IJobEventStore>().ShouldBeOfType<FileJobEventStore>();
            provider.GetRequiredService<JobEventOptions>().Directory.ShouldBe(dir);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddInMemoryJobEvents_registers_a_resolvable_in_memory_store()
    {
        using var provider = new ServiceCollection()
            .AddInMemoryJobEvents(o => o.MaxEventsPerJob = 5)
            .BuildServiceProvider();

        provider.GetRequiredService<IJobEventStore>().ShouldBeOfType<InMemoryJobEventStore>();
        provider.GetRequiredService<JobEventOptions>().MaxEventsPerJob.ShouldBe(5);
    }

    [Fact]
    public void No_registration_leaves_IJobEventStore_unresolvable()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        provider.GetService<IJobEventStore>().ShouldBeNull();
    }
}
