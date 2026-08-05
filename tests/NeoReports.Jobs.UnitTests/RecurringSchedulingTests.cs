using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Scheduling;
using NeoReports.Jobs.DependencyInjection;
using Shouldly;
using Xunit;

namespace NeoReports.Jobs.UnitTests;

/// <summary>
/// ADR D41: <see cref="InMemoryJobScheduler"/>'s <see cref="IRecurringReportScheduler"/> facet —
/// registration bookkeeping, next-occurrence computation, and removal. The actual "fires within a
/// minute" behavior (real-time PeriodicTimer loop against wall-clock cron boundaries) is verified
/// manually via the live sample, the same way other epics validated real-time UI behavior.
/// </summary>
public class RecurringSchedulingTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReport<Sale>("sales", b => b
            .From(new ControllableSource(totalRows: 0, pageSize: 10, perPageDelay: TimeSpan.Zero))
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new NullWriterFactory())));
        services.AddNeoReportsInMemoryJobs();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task InMemoryJobScheduler_is_resolvable_as_both_interfaces_from_the_same_instance()
    {
        await using var provider = BuildProvider();

        var jobScheduler = provider.GetRequiredService<IReportJobScheduler>();
        var recurringScheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        recurringScheduler.ShouldBeSameAs(jobScheduler);
    }

    [Fact]
    public async Task Register_computes_a_plausible_next_occurrence()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);
        DateTimeOffset? next = await scheduler.GetNextOccurrenceAsync("sales", CancellationToken.None);

        next.ShouldNotBeNull();
        next!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        next.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(1.1));
    }

    [Fact]
    public async Task Unregistered_report_has_no_next_occurrence()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        (await scheduler.GetNextOccurrenceAsync("sales", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Remove_stops_tracking_the_schedule()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);
        await scheduler.RemoveRecurringAsync("sales", CancellationToken.None);

        (await scheduler.GetNextOccurrenceAsync("sales", CancellationToken.None)).ShouldBeNull();
        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldNotContain("sales");
    }

    [Fact]
    public async Task Remove_of_an_unregistered_report_is_a_no_op()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        await Should.NotThrowAsync(() => scheduler.RemoveRecurringAsync("does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task Register_replaces_an_existing_schedule_for_the_same_report()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        await scheduler.RegisterRecurringAsync("sales", "0 0 1 1 *", CancellationToken.None);
        await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);

        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).Count(n => n == "sales").ShouldBe(1);
        DateTimeOffset? next = await scheduler.GetNextOccurrenceAsync("sales", CancellationToken.None);
        next!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(1.1));
    }

    [Fact]
    public async Task Register_rejects_an_invalid_cron_expression()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        await Should.ThrowAsync<ConfigurationException>(
            () => scheduler.RegisterRecurringAsync("sales", "garbage", CancellationToken.None));
    }

    [Fact]
    public async Task ListRegisteredNames_reflects_current_registrations()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldBeEmpty();

        await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);
        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldBe(new[] { "sales" });
    }

    /// <summary>
    /// Registering is remove-then-add, which a <c>ConcurrentDictionary</c> cannot make atomic, so it
    /// is now serialized by a lock. This hammers that path from many threads: it guards the risk the
    /// lock itself introduces (a deadlock, or a corrupted final state) and pins that concurrent
    /// registration converges on exactly one live registration.
    /// <para>
    /// It cannot observe the leak the lock fixes — the loser's loop keeps running untracked, and
    /// nothing public exposes it. Making that assertable needs a clock abstraction so the real-time
    /// loop can be driven deterministically; see the PR for why that is a separate change.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_registrations_for_one_report_converge_on_a_single_registration()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            scheduler.RegisterRecurringAsync("sales", i % 2 == 0 ? "* * * * *" : "*/5 * * * *", CancellationToken.None))));

        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldBe(new[] { "sales" });

        // Removal must still work afterwards — a lock held by a faulted registration would show up
        // here as a hang rather than a failure, which the test framework's own timeout catches.
        await scheduler.RemoveRecurringAsync("sales", CancellationToken.None);
        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Interleaved_registration_and_removal_leave_a_consistent_state()
    {
        await using var provider = BuildProvider();
        var scheduler = provider.GetRequiredService<IRecurringReportScheduler>();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            if (i % 2 == 0)
                await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);
            else
                await scheduler.RemoveRecurringAsync("sales", CancellationToken.None);
        })));

        // Either outcome is legitimate depending on who ran last; what must not happen is a torn
        // state, an exception, or a hang.
        IReadOnlyList<string> names = await scheduler.ListRegisteredNamesAsync(CancellationToken.None);
        names.Count.ShouldBeLessThanOrEqualTo(1);
    }
}
