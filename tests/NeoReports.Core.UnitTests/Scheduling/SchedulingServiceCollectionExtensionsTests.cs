using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Scheduling;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Scheduling;

/// <summary>ADR D41: <c>AddScheduling</c> / <c>AddInMemoryScheduling</c> DI registration.</summary>
public class SchedulingServiceCollectionExtensionsTests
{
    [Fact]
    public void AddScheduling_registers_a_resolvable_file_backed_store()
    {
        var dir = Path.Join(Path.GetTempPath(), "nr-d41-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new ServiceCollection()
                .AddScheduling(o => o.Directory = dir)
                .BuildServiceProvider();

            provider.GetRequiredService<IScheduleOverrideStore>().ShouldBeOfType<FileScheduleOverrideStore>();
            provider.GetRequiredService<ScheduleOverrideOptions>().Directory.ShouldBe(dir);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddInMemoryScheduling_registers_a_resolvable_in_memory_store()
    {
        using var provider = new ServiceCollection()
            .AddInMemoryScheduling()
            .BuildServiceProvider();

        provider.GetRequiredService<IScheduleOverrideStore>().ShouldBeOfType<InMemoryScheduleOverrideStore>();
    }

    [Fact]
    public void No_registration_leaves_IScheduleOverrideStore_unresolvable()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        provider.GetService<IScheduleOverrideStore>().ShouldBeNull();
    }
}
