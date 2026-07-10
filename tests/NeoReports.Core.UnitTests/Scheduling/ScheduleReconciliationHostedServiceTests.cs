using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Registry;
using NeoReports.Core.Scheduling;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Scheduling;

/// <summary>ADR D41: startup reconciliation of declared × override schedules, plus orphan cleanup.</summary>
public class ScheduleReconciliationHostedServiceTests
{
    private sealed class FakeRecurringScheduler : IRecurringReportScheduler
    {
        public List<(string Name, string Cron)> Registered { get; } = new();
        public List<string> Removed { get; } = new();
        public List<string> OrphansToReport { get; set; } = new();

        public Task RegisterRecurringAsync(string reportName, string cron, CancellationToken cancellationToken)
        {
            Registered.Add((reportName, cron));
            return Task.CompletedTask;
        }

        public Task RemoveRecurringAsync(string reportName, CancellationToken cancellationToken)
        {
            Removed.Add(reportName);
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetNextOccurrenceAsync(string reportName, CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task<IReadOnlyList<string>> ListRegisteredNamesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(OrphansToReport);
    }

    private static IReportRegistry BuildRegistry(Action<IServiceCollection> configureReports)
    {
        var services = new ServiceCollection();
        configureReports(services);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IReportRegistry>();
    }

    private static void AddPlainReport(IServiceCollection services, string name, string? cron = null) =>
        services.AddReport<Sale>(name, b =>
        {
            b.From(new FakeBatchSource<Sale>(Array.Empty<IReadOnlyList<Sale>>()))
                .Column(v => v.Id, "Id")
                .To(new OutputSpec(new FakeWriterFactory()));
            if (cron is not null)
                b.Schedule(cron);
        });

    [Fact]
    public async Task Registers_a_declared_schedule()
    {
        IReportRegistry registry = BuildRegistry(s => AddPlainReport(s, "a", "0 6 * * 1"));
        var overrides = new InMemoryScheduleOverrideStore();
        var scheduler = new FakeRecurringScheduler();

        var service = new ScheduleReconciliationHostedService(registry, overrides, NullLogger<ScheduleReconciliationHostedService>.Instance, scheduler);
        await service.StartAsync(CancellationToken.None);

        scheduler.Registered.ShouldContain(("a", "0 6 * * 1"));
    }

    [Fact]
    public async Task Applies_an_override_where_there_is_no_declaration()
    {
        IReportRegistry registry = BuildRegistry(s => AddPlainReport(s, "b"));
        var overrides = new InMemoryScheduleOverrideStore();
        await overrides.SaveAsync("b", new ScheduleOverrideEntry("*/5 * * * *"), CancellationToken.None);
        var scheduler = new FakeRecurringScheduler();

        var service = new ScheduleReconciliationHostedService(registry, overrides, NullLogger<ScheduleReconciliationHostedService>.Instance, scheduler);
        await service.StartAsync(CancellationToken.None);

        scheduler.Registered.ShouldContain(("b", "*/5 * * * *"));
    }

    [Fact]
    public async Task Tombstone_override_removes_rather_than_registers_a_declared_schedule()
    {
        IReportRegistry registry = BuildRegistry(s => AddPlainReport(s, "c", "0 6 * * 1"));
        var overrides = new InMemoryScheduleOverrideStore();
        await overrides.SaveAsync("c", new ScheduleOverrideEntry(null), CancellationToken.None);
        var scheduler = new FakeRecurringScheduler();

        var service = new ScheduleReconciliationHostedService(registry, overrides, NullLogger<ScheduleReconciliationHostedService>.Instance, scheduler);
        await service.StartAsync(CancellationToken.None);

        scheduler.Removed.ShouldContain("c");
        scheduler.Registered.ShouldNotContain(r => r.Name == "c");
    }

    [Fact]
    public async Task Removes_orphaned_registrations_for_reports_no_longer_registered()
    {
        // "still-here" has an active declared schedule, so it's re-registered (not removed) by the
        // main reconciliation loop; "long-gone" is only known via the scheduler's own storage — it
        // has no matching report in the registry at all, so it's an orphan.
        IReportRegistry registry = BuildRegistry(s => AddPlainReport(s, "still-here", "0 6 * * 1"));
        var overrides = new InMemoryScheduleOverrideStore();
        var scheduler = new FakeRecurringScheduler { OrphansToReport = new List<string> { "still-here", "long-gone" } };

        var service = new ScheduleReconciliationHostedService(registry, overrides, NullLogger<ScheduleReconciliationHostedService>.Instance, scheduler);
        await service.StartAsync(CancellationToken.None);

        scheduler.Removed.ShouldContain("long-gone");
        scheduler.Removed.ShouldNotContain("still-here");
        scheduler.Registered.ShouldContain(("still-here", "0 6 * * 1"));
    }

    [Fact]
    public async Task No_recurring_scheduler_registered_is_a_no_op()
    {
        IReportRegistry registry = BuildRegistry(s => AddPlainReport(s, "a", "0 6 * * 1"));
        var overrides = new InMemoryScheduleOverrideStore();

        var service = new ScheduleReconciliationHostedService(registry, overrides, NullLogger<ScheduleReconciliationHostedService>.Instance, scheduler: null);
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }
}
