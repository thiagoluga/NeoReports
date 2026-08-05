using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
/// registration bookkeeping, next-occurrence computation, and removal — plus, since D76, the firing
/// loop itself. That loop used to be untestable in practice (Cronos granularity is one minute, so
/// every assertion would have cost a wall-clock minute of CI) and carried a "verified manually via
/// the live sample" caveat; the scheduler now takes a <c>TimeProvider</c>, so a fake clock drives it
/// deterministically instead.
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

    /// <summary>
    /// Builds a scheduler on a fake clock. The clock starts at a whole minute so a "* * * * *"
    /// schedule's next occurrence is exactly 60s away, which keeps the advances below unambiguous.
    /// </summary>
    private static (InMemoryJobScheduler Scheduler, FakeTimeProvider Clock, RecordingJobStore Store) BuildOnFakeClock(
        Func<Task>? onRun = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new RecordingJobStore(onRun);
        var worker = new ReportJobWorker(
            new NoOpRunner(), store, NullLogger<ReportJobWorker>.Instance, jobEvents: null);

        return (new InMemoryJobScheduler(store, worker, logger: null, timeProvider: clock), clock, store);
    }

    /// <summary>
    /// Advances the fake clock in steps until <paramref name="condition"/> holds, yielding between
    /// steps. The clock makes the loop's *waiting* deterministic, but the loop still runs on the
    /// thread pool: advancing in one jump can land before the loop has reached its next wait, and the
    /// time would then be consumed by nothing. Stepping tolerates that without an unbounded wait.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> condition, string what)
    {
        for (var i = 0; i < 60; i++)
        {
            if (condition())
                return;

            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for {what} while advancing the clock.");
    }

    [Fact]
    public async Task The_loop_fires_when_the_clock_reaches_the_next_occurrence()
    {
        (InMemoryJobScheduler scheduler, FakeTimeProvider clock, RecordingJobStore store) = BuildOnFakeClock();
        await using var _ = scheduler;

        await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);

        store.Created.ShouldBe(0, "nothing is due yet");

        await AdvanceUntilAsync(clock, () => store.Created >= 1, "the first firing");

        store.Created.ShouldBe(1);
    }

    [Fact]
    public async Task A_firing_that_throws_does_not_end_the_schedule()
    {
        // The whole point of D76's catch-all: before it, one bad firing faulted the fire-and-forget
        // task and the report never fired again for the life of the process — silently, with the API
        // still reporting the schedule as registered.
        var failNext = true;
        (InMemoryJobScheduler scheduler, FakeTimeProvider clock, RecordingJobStore store) = BuildOnFakeClock(
            onRun: () =>
            {
                if (!failNext)
                    return Task.CompletedTask;

                failNext = false;
                throw new InvalidOperationException("Simulated store failure on the first firing.");
            });
        await using var _ = scheduler;

        await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);

        // First occurrence throws...
        await AdvanceUntilAsync(clock, () => store.Attempts >= 1, "the first (failing) firing");

        // ...and the loop must come back: the back-off elapses, the next occurrence fires normally.
        await AdvanceUntilAsync(clock, () => store.Created >= 1, "a firing after the failure");

        store.Created.ShouldBe(1, "the schedule survived the failed firing and fired again");
    }

    [Fact]
    public async Task A_schedule_that_can_never_occur_again_ends_its_loop()
    {
        // 30 February is syntactically valid and never happens, so Cronos returns no next occurrence.
        // The loop must end rather than spin computing a date that will not come.
        (InMemoryJobScheduler scheduler, FakeTimeProvider clock, RecordingJobStore store) = BuildOnFakeClock();
        await using var _ = scheduler;

        await scheduler.RegisterRecurringAsync("sales", "0 0 30 2 *", CancellationToken.None);

        clock.Advance(TimeSpan.FromDays(400));
        await Task.Delay(100);

        store.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Removing_a_schedule_while_it_is_backing_off_after_a_failure_stops_it()
    {
        // The back-off is a wait like any other, so removal has to interrupt it — otherwise a failing
        // schedule keeps a loop alive for up to the back-off after the operator removed it.
        (InMemoryJobScheduler scheduler, FakeTimeProvider clock, RecordingJobStore store) = BuildOnFakeClock(
            onRun: () => throw new InvalidOperationException("Simulated store failure on every firing."));
        await using var _ = scheduler;

        await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);
        await AdvanceUntilAsync(clock, () => store.Attempts >= 1, "the first (failing) firing");

        await scheduler.RemoveRecurringAsync("sales", CancellationToken.None);
        int attemptsAtRemoval = store.Attempts;

        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(100);

        store.Attempts.ShouldBe(attemptsAtRemoval, "the loop stopped instead of firing again after the back-off");
    }

    [Fact]
    public async Task Removing_a_schedule_stops_it_firing()
    {
        (InMemoryJobScheduler scheduler, FakeTimeProvider clock, RecordingJobStore store) = BuildOnFakeClock();
        await using var _ = scheduler;

        await scheduler.RegisterRecurringAsync("sales", "* * * * *", CancellationToken.None);
        await scheduler.RemoveRecurringAsync("sales", CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);

        store.Created.ShouldBe(0);
    }
}
